/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this library; if not, see <https://www.gnu.org/licenses/lgpl-3.0.html>.
 */

/*
 * WebChatDialog.cs
 * Provides a dialog-based chat interface using WebView for rendering HTML content.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using Newtonsoft.Json;
using Rhino;
using Rhino.UI;
using SmartHopper.Infrastructure.AICall.Core.Base;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.Core.Requests;
using SmartHopper.Infrastructure.AICall.Core.Returns;
using SmartHopper.Infrastructure.AICall.Metrics;
using SmartHopper.Infrastructure.AICall.Sessions;
using SmartHopper.Infrastructure.AICall.Utilities;
using SmartHopper.Infrastructure.Authentication.Codex;
using SmartHopper.Infrastructure.Diagnostics;
using SmartHopper.Infrastructure.Settings;
using SmartHopper.Infrastructure.Streaming;

namespace SmartHopper.Core.UI.Chat
{
    /// <summary>
    /// Dialog-based chat interface using WebView for rendering HTML content.
    /// </summary>
    internal partial class WebChatDialog : Form
    {
        // Cache platform check result (platform never changes at runtime)
        private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        // UI Component: full WebView-based UI
        private readonly WebView _webView = null!;

        // Chat Dialog
        private readonly HtmlChatRenderer _htmlRenderer = new HtmlChatRenderer();
        private bool _webViewInitialized;
        private TaskCompletionSource<bool> _webViewInitializedTcs = new TaskCompletionSource<bool>();
        private IConversationSession _currentSession = null!;
        private System.Threading.CancellationTokenSource? _currentCts;
        private string? _pendingUserMessage;
        private DropDown? _codexModelDropDown;
        private DropDown? _codexReasoningDropDown;
        private Label? _codexConfigurationStatus;
        private DropDown? _codexEditingModeDropDown;
        private Label? _codexCanvasStatus;
        private Panel? _codexApprovalPanel;
        private Label? _codexApprovalText;
        private Button? _codexApprovalApplyButton;
        private Button? _codexApprovalCancelButton;
        private IReadOnlyList<CodexModelOption> _codexModels = Array.Empty<CodexModelOption>();
        private bool _updatingCodexSelectors;

        // Keeps last-rendered HTML per DOM key to make upserts idempotent and avoid redundant DOM work
        // Uses FIFO eviction to prevent unbounded growth in long conversations
        private readonly Dictionary<string, string> _lastDomHtmlByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Queue<string> _idempotencyFifo = new Queue<string>();
        private const int MaxIdempotencyCacheSize = 100;

        // ConversationSession manages all history and requests
        // WebChatDialog is now a pure UI consumer
        private bool _isDomUpdating;
        private readonly Queue<Action> _domUpdateQueue = new Queue<Action>();

        private int _activeScripts;

        private readonly object _htmlRenderLock = new object();

        private readonly ChatRenderScheduler _renderScheduler = new ChatRenderScheduler();
        private volatile bool _isClosing;

        // When the user is moving/resizing the window, defer DOM updates to avoid UI-thread stalls.
        private DateTime _deferDomUpdatesUntilUtc = DateTime.MinValue;
        private bool _domDrainScheduled;
        private const int DomDeferDuringMoveResizeMs = 400;
        private const int DomDrainBatchSize = 10;
        private const int DomDrainDebounceMs = 16;

        private const int MAX_CONCURRENT_SCRIPTS = 4;

        // Status text to apply after the document is fully loaded
        private string _pendingStatusAfter = "Ready";

        private readonly TaskCompletionSource<bool> _initialHistoryReplayTcs = new TaskCompletionSource<bool>();

        // Greeting behavior: when true, the dialog will request a greeting from ConversationSession on init
        private readonly bool _generateGreeting;

        /// <summary>
        /// Gets the Grasshopper document currently bound to this dialog's Canvas tools.
        /// A null value means that the dialog was created without an active Canvas.
        /// </summary>
        internal Guid? ConnectedCanvasDocumentId =>
            (this._currentSession as ICodexCanvasToolSession)?.CanvasTools?.DocumentId;

        [Conditional("DEBUG")]
        private static void DebugLog(string message)
        {
            Debug.WriteLine(message);
        }

        /// <summary>
        /// Creates a new WebChatDialog bound to an initial AI request and optional progress reporter.
        /// </summary>
        /// <param name="request">The initial AI request used to seed the conversation session.</param>
        /// <param name="progressReporter">Optional progress callback for reporting UI status.</param>
        /// <param name="generateGreeting">When true, the dialog requests the session to emit an initial greeting (if enabled in settings).</param>
        internal WebChatDialog(
            AIRequestCall request,
            Action<string>? progressReporter,
            bool generateGreeting = false,
            Func<IConversationObserver, IConversationSession>? sessionFactory = null)
        {
            try
            {
                this._generateGreeting = generateGreeting;

                var mainWindow = RhinoEtoApp.MainWindow;
                if (mainWindow != null)
                {
                    this.Owner = mainWindow;
                    this.ShowInTaskbar = false;
                }

                // Create session with attached observer from the start
                var observer = new WebChatObserver(this);
                this._currentSession = sessionFactory != null
                    ? sessionFactory(observer)
                    : new ConversationSession(request, observer, generateGreeting: this._generateGreeting);

                // Window basics
                this.ClientSize = new Size(720, 640);
                this.MinimumSize = new Size(560, 420);
                this.Padding = new Padding(6);

                // 대화 본문과 Codex 전용 실행 옵션을 구성합니다.
                this._webView = new WebView();
                this._webView.DocumentLoaded += this.WebView_DocumentLoaded;
                this._webView.DocumentLoading += this.WebView_DocumentLoading;
                if (this._currentSession is ICodexConfigurableConversationSession codexSession)
                {
                    var content = new DynamicLayout { Spacing = new Size(0, 6) };
                    content.Add(this.CreateCodexConfigurationPanel(), yscale: false);
                    content.Add(this._webView, yscale: true);
                    this.Content = content;
                    if (this._currentSession is ICodexCanvasToolSession canvasSession && canvasSession.CanvasTools != null)
                    {
                        canvasSession.CanvasTools.ApprovalHandler = this.RequestCanvasToolApprovalAsync;
                        this.PopulateCanvasToolConfiguration(canvasSession.CanvasTools);
                    }
                    else if (this._codexCanvasStatus != null)
                    {
                        this._codexCanvasStatus.Text = "연결 안 됨: 활성 Grasshopper Canvas가 없습니다.";
                    }
                    _ = this.LoadCodexConfigurationAsync(codexSession);
                }
                else
                {
                    this.Content = this._webView;
                }

                // If the user drags/resizes the dialog while we are rendering/upserting messages,
                // defer DOM work to keep Rhino/Eto responsive.
                this.LocationChanged += (_, __) => this.MarkMoveResizeInteraction();
                this.SizeChanged += (_, __) => this.MarkMoveResizeInteraction();

                // Cancel ongoing AI calls when dialog closes
                this.Closing += (_, e) => this.OnDialogClosing();

                // Initialize web view and optionally start greeting
                _ = this.InitializeWebViewAsync();
            }
            catch (Exception ex)
            {
                DebugLog($"[WebChatDialog] Constructor error: {ex.Message}");
            }
        }

        private Control CreateCodexConfigurationPanel()
        {
            this._codexModelDropDown = new DropDown { Enabled = false };
            this._codexModelDropDown.Items.Add(new ListItem { Text = "모델 불러오는 중...", Key = string.Empty });
            this._codexModelDropDown.SelectedIndex = 0;
            this._codexModelDropDown.SelectedIndexChanged += (_, __) => this.OnCodexModelChanged();

            this._codexReasoningDropDown = new DropDown { Enabled = false };
            this._codexReasoningDropDown.Items.Add(new ListItem { Text = "추론 수준 불러오는 중...", Key = string.Empty });
            this._codexReasoningDropDown.SelectedIndex = 0;
            this._codexReasoningDropDown.SelectedIndexChanged += (_, __) => this.ApplyCodexConfiguration();

            this._codexConfigurationStatus = new Label
            {
                Text = "Codex 모델 목록을 불러오는 중입니다.",
                TextColor = Colors.Gray,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var selectors = new TableLayout
            {
                Spacing = new Size(8, 4),
                Rows =
                {
                    new TableRow(
                        new Label { Text = "모델", VerticalAlignment = VerticalAlignment.Center },
                        new TableCell(this._codexModelDropDown, true),
                        new Label { Text = "추론 수준", VerticalAlignment = VerticalAlignment.Center },
                        new TableCell(this._codexReasoningDropDown, true)),
                },
            };

            var panelLayout = new DynamicLayout { Spacing = new Size(0, 4) };
            panelLayout.Add(selectors, yscale: false);
            panelLayout.Add(this._codexConfigurationStatus, yscale: false);

            this._codexEditingModeDropDown = new DropDown { Enabled = false };
            this._codexEditingModeDropDown.Items.Add(new ListItem { Text = "읽기 전용", Key = "ReadOnly" });
            this._codexEditingModeDropDown.Items.Add(new ListItem { Text = "변경 전 확인", Key = "ConfirmChanges" });
            this._codexEditingModeDropDown.SelectedIndex = 0;
            this._codexCanvasStatus = new Label { TextColor = Colors.Gray };
            this._codexApprovalText = new Label { Wrap = WrapMode.Word };
            this._codexApprovalApplyButton = new Button { Text = "적용" };
            this._codexApprovalCancelButton = new Button { Text = "취소" };
            this._codexApprovalPanel = new Panel
            {
                Visible = false,
                Padding = new Padding(8),
                BackgroundColor = Color.FromArgb(255, 247, 224),
                Content = new TableLayout
                {
                    Spacing = new Size(0, 6),
                    Rows =
                    {
                        new TableRow(new TableCell(this._codexApprovalText, true)),
                        new TableRow(new StackLayout
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 6,
                            Items = { this._codexApprovalApplyButton, this._codexApprovalCancelButton },
                        }),
                    },
                },
            };
            panelLayout.Add(new TableLayout
            {
                Spacing = new Size(8, 4),
                Rows =
                {
                    new TableRow(
                        new Label { Text = "Canvas 편집", VerticalAlignment = VerticalAlignment.Center },
                        new TableCell(this._codexEditingModeDropDown, false),
                        new TableCell(this._codexCanvasStatus, true)),
                },
            }, yscale: false);
            panelLayout.Add(this._codexApprovalPanel, yscale: false);

            return new Panel
            {
                Padding = new Padding(10, 7),
                BackgroundColor = Color.FromArgb(245, 247, 250),
                Content = panelLayout,
            };
        }

        private void PopulateCanvasToolConfiguration(ICodexCanvasToolBridge bridge)
        {
            if (this._codexEditingModeDropDown == null || this._codexCanvasStatus == null)
            {
                return;
            }

            bridge.EditingMode = CodexCanvasEditingMode.ReadOnly;
            bridge.BuildDynamicTools();
            this._codexEditingModeDropDown.SelectedIndex = 0;
            this._codexEditingModeDropDown.Enabled = true;
            this._codexCanvasStatus.Text = $"연결: {bridge.CanvasName} · 읽기 전용 · 도구 {bridge.AvailableToolCount}개";
            this._codexEditingModeDropDown.SelectedIndexChanged += (_, __) =>
            {
                bridge.EditingMode = this._codexEditingModeDropDown.SelectedKey == "ConfirmChanges"
                    ? CodexCanvasEditingMode.ConfirmChanges
                    : CodexCanvasEditingMode.ReadOnly;
                bridge.BuildDynamicTools();
                this._codexCanvasStatus.Text = bridge.EditingMode == CodexCanvasEditingMode.ReadOnly
                    ? $"연결: {bridge.CanvasName} · 읽기 전용 · 도구 {bridge.AvailableToolCount}개"
                    : $"연결: {bridge.CanvasName} · 변경마다 확인 · 도구 {bridge.AvailableToolCount}개";
            };
        }

        private Task<bool> RequestCanvasToolApprovalAsync(
            CodexCanvasToolApprovalRequest request,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration registration = default;
            RhinoApp.InvokeOnUiThread(() =>
            {
                if (this._codexApprovalPanel == null ||
                    this._codexApprovalText == null ||
                    this._codexApprovalApplyButton == null ||
                    this._codexApprovalCancelButton == null)
                {
                    completion.TrySetResult(false);
                    return;
                }

                EventHandler<EventArgs>? apply = null;
                EventHandler<EventArgs>? cancel = null;
                void Complete(bool approved)
                {
                    this._codexApprovalApplyButton.Click -= apply;
                    this._codexApprovalCancelButton.Click -= cancel;
                    this._codexApprovalPanel.Visible = false;
                    completion.TrySetResult(approved);
                }

                apply = (_, __) => Complete(true);
                cancel = (_, __) => Complete(false);
                this._codexApprovalApplyButton.Click += apply;
                this._codexApprovalCancelButton.Click += cancel;
                this._codexApprovalText.Text =
                    $"Canvas 변경 승인 요청\n도구: {request.ToolName}\n요약: {request.Summary}\n영향 GUID: {request.AffectedGuidCount}개\n위험도: {request.Risk}";
                this._codexApprovalPanel.Visible = true;
                registration = cancellationToken.Register(() => RhinoApp.InvokeOnUiThread(() => Complete(false)));
            });

            return completion.Task.ContinueWith(
                task =>
                {
                    registration.Dispose();
                    return task.Result;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task LoadCodexConfigurationAsync(ICodexConfigurableConversationSession session)
        {
            try
            {
                var models = await session.GetModelsAsync().ConfigureAwait(false);
                RhinoApp.InvokeOnUiThread(() => this.PopulateCodexModels(session, models));
            }
            catch (Exception ex)
            {
                DebugLog($"[WebChatDialog] Codex model list error: {ex.Message}");
                RhinoApp.InvokeOnUiThread(() =>
                {
                    if (this._codexConfigurationStatus != null)
                    {
                        this._codexConfigurationStatus.Text = "모델 목록을 불러오지 못했습니다. 기본 설정으로 대화할 수 있습니다.";
                    }
                });
            }
        }

        private void PopulateCodexModels(
            ICodexConfigurableConversationSession session,
            IReadOnlyList<CodexModelOption> models)
        {
            if (this._codexModelDropDown == null || this._codexReasoningDropDown == null)
            {
                return;
            }

            this._updatingCodexSelectors = true;
            try
            {
                this._codexModels = models;
                this._codexModelDropDown.Items.Clear();
                foreach (var model in models)
                {
                    var suffix = model.IsDefault ? " · 기본" : string.Empty;
                    this._codexModelDropDown.Items.Add(new ListItem
                    {
                        Text = $"{model.DisplayName}{suffix}",
                        Key = model.Model,
                    });
                }

                var savedModel = SmartHopperSettings.Instance.SmartHopperAssistant?.CodexModel;
                var selectedModel = models.FirstOrDefault(item =>
                        string.Equals(item.Model, savedModel, StringComparison.OrdinalIgnoreCase))
                    ?? models.FirstOrDefault(item =>
                        string.Equals(item.Model, session.SelectedModel, StringComparison.OrdinalIgnoreCase))
                    ?? models.FirstOrDefault(item => item.IsDefault)
                    ?? models.FirstOrDefault();

                if (selectedModel == null)
                {
                    this._codexModelDropDown.Items.Add(new ListItem { Text = "사용 가능한 모델 없음", Key = string.Empty });
                    this._codexModelDropDown.SelectedIndex = 0;
                    this._codexConfigurationStatus!.Text = "현재 계정에서 사용할 수 있는 Codex 모델이 없습니다.";
                    return;
                }

                this.SelectDropDownKey(this._codexModelDropDown, selectedModel.Model);
                var savedEffort = SmartHopperSettings.Instance.SmartHopperAssistant?.CodexReasoningEffort;
                this.PopulateCodexReasoningOptions(
                    selectedModel,
                    string.IsNullOrWhiteSpace(savedEffort) ? session.SelectedReasoningEffort : savedEffort);
                this._codexModelDropDown.Enabled = true;
                this._codexReasoningDropDown.Enabled = selectedModel.SupportedReasoningEfforts.Count > 0;
                this._codexConfigurationStatus!.Text = "선택값은 Canvas Assistant와 기본 AI 컴포넌트에 적용됩니다.";
                session.Configure(
                    selectedModel.Model,
                    this._codexReasoningDropDown.SelectedKey);
                this.SaveCodexConfiguration(selectedModel.Model, this._codexReasoningDropDown.SelectedKey);
            }
            finally
            {
                this._updatingCodexSelectors = false;
            }
        }

        private void OnCodexModelChanged()
        {
            if (this._updatingCodexSelectors || this._codexModelDropDown == null)
            {
                return;
            }

            var model = this._codexModels.FirstOrDefault(item =>
                string.Equals(item.Model, this._codexModelDropDown.SelectedKey, StringComparison.Ordinal));
            if (model == null)
            {
                return;
            }

            this._updatingCodexSelectors = true;
            try
            {
                this.PopulateCodexReasoningOptions(model, model.DefaultReasoningEffort);
                if (this._codexReasoningDropDown != null)
                {
                    this._codexReasoningDropDown.Enabled = model.SupportedReasoningEfforts.Count > 0;
                }
            }
            finally
            {
                this._updatingCodexSelectors = false;
            }

            this.ApplyCodexConfiguration();
        }

        private void PopulateCodexReasoningOptions(CodexModelOption model, string selectedEffort)
        {
            if (this._codexReasoningDropDown == null)
            {
                return;
            }

            this._codexReasoningDropDown.Items.Clear();
            foreach (var effort in model.SupportedReasoningEfforts)
            {
                this._codexReasoningDropDown.Items.Add(new ListItem
                {
                    Text = $"{ToReasoningLabel(effort.Value)} ({effort.Value})",
                    Key = effort.Value,
                });
            }

            var effortToSelect = model.SupportedReasoningEfforts.Any(item =>
                    string.Equals(item.Value, selectedEffort, StringComparison.OrdinalIgnoreCase))
                ? selectedEffort
                : model.DefaultReasoningEffort;
            this.SelectDropDownKey(this._codexReasoningDropDown, effortToSelect);
            if (this._codexReasoningDropDown.SelectedIndex < 0 && this._codexReasoningDropDown.Items.Count > 0)
            {
                this._codexReasoningDropDown.SelectedIndex = 0;
            }
        }

        private void ApplyCodexConfiguration()
        {
            if (this._updatingCodexSelectors ||
                this._currentSession is not ICodexConfigurableConversationSession session ||
                this._codexModelDropDown == null ||
                this._codexReasoningDropDown == null)
            {
                return;
            }

            session.Configure(this._codexModelDropDown.SelectedKey, this._codexReasoningDropDown.SelectedKey);
            this.SaveCodexConfiguration(this._codexModelDropDown.SelectedKey, this._codexReasoningDropDown.SelectedKey);
        }

        private void SaveCodexConfiguration(string? model, string? reasoningEffort)
        {
            var settings = SmartHopperSettings.Instance;
            var assistant = settings.SmartHopperAssistant ??= new SmartHopperAssistantSettings();
            assistant.AssistantProvider = "Codex Account";
            assistant.AssistantModel = model?.Trim() ?? string.Empty;
            assistant.CodexModel = model?.Trim() ?? string.Empty;
            assistant.CodexReasoningEffort = reasoningEffort?.Trim() ?? string.Empty;
            settings.DefaultAIProvider = "Codex Account";
            settings.Save();
        }

        private void SelectDropDownKey(DropDown dropDown, string? key)
        {
            dropDown.SelectedIndex = -1;
            for (var index = 0; index < dropDown.Items.Count; index++)
            {
                if (string.Equals(dropDown.Items[index].Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    dropDown.SelectedIndex = index;
                    return;
                }
            }
        }

        private static string ToReasoningLabel(string value)
        {
            return value.ToLowerInvariant() switch
            {
                "none" => "없음",
                "minimal" => "최소",
                "low" => "낮음",
                "medium" => "보통",
                "high" => "높음",
                "xhigh" => "매우 높음",
                "max" => "최대",
                "ultra" => "울트라",
                _ => value,
            };
        }

        /// <summary>
        /// Upserts a message identified by domKey immediately after the message identified by followKey.
        /// If followKey is not found, it falls back to a normal upsert by domKey.
        /// Uses the same idempotency cache by domKey to avoid redundant DOM work.
        /// </summary>
        /// <param name="followKey">The DOM key of the message after which the new/upserted message should be placed.</param>
        /// <param name="domKey">The stable DOM key used to perform an idempotent upsert of the message.</param>
        /// <param name="interaction">The interaction to render into HTML and insert into the DOM.</param>
        private void UpsertMessageAfter(string followKey, string domKey, IAIInteraction interaction)
        {
            if (interaction == null || string.IsNullOrWhiteSpace(domKey))
            {
                return;
            }

            _ = this.QueueInteractionRenderAsync(domKey, interaction, followKey);
        }

        /// <summary>
        /// Ensures the dialog is visible on screen.
        /// </summary>
        internal void EnsureVisibility()
        {
            try
            {
                if (!this.Visible)
                {
                    this.Show();
                }
                else
                {
                    this.BringToFront();
                }
            }
            catch (Exception ex)
            {
                DebugLog($"[WebChatDialog] EnsureVisibility error: {ex.Message}");
            }
        }

        /// <summary>
        /// Minimal query string parser (avoids System.Web dependency).
        /// </summary>
        /// <param name="query">The query string to parse (with or without leading '?').</param>
        /// <returns>A dictionary containing parsed key/value pairs.</returns>
        private static Dictionary<string, string> ParseQueryString(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query))
            {
                return dict;
            }

            var q = query.StartsWith("?") ? query.Substring(1) : query;
            foreach (var pair in q.Split('&'))
            {
                if (string.IsNullOrEmpty(pair))
                {
                    continue;
                }

                var kv = pair.Split(new[] { '=' }, 2);
                var key = Uri.UnescapeDataString(kv[0] ?? string.Empty);
                var val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
                if (!string.IsNullOrEmpty(key))
                {
                    dict[key] = val;
                }
            }

            return dict;
        }

        /// <summary>
        /// Runs the given action when the WebView is initialized. Always marshals to Rhino UI thread.
        /// Also serializes DOM updates to avoid reentrancy.
        /// </summary>
        /// <param name="action">The action to execute once the WebView is ready.</param>
        private void RunWhenWebViewReady(Action action)
        {
            if (action == null || this._isClosing)
            {
                return;
            }

            void EnqueueAndScheduleDrain()
            {
                if (this._isClosing) return;
                this._domUpdateQueue.Enqueue(action);
                this.ScheduleDomDrain();
            }

            if (this._webViewInitialized)
            {
                RhinoApp.InvokeOnUiThread(EnqueueAndScheduleDrain);
            }
            else
            {
                this._webViewInitializedTcs.Task.ContinueWith(
                    _ => RhinoApp.InvokeOnUiThread(EnqueueAndScheduleDrain),
                    System.Threading.CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
        }

        /// <summary>
        /// Marks the window as being moved/resized, deferring DOM updates.
        /// </summary>
        private void MarkMoveResizeInteraction()
        {
            try
            {
                this._deferDomUpdatesUntilUtc = DateTime.UtcNow.AddMilliseconds(DomDeferDuringMoveResizeMs);
                this.ScheduleDomDrain();
            }
            catch (Exception ex)
            {
                DebugLog($"[WebChatDialog] MarkMoveResizeInteraction error: {ex.Message}");
            }
        }

        /// <summary>
        /// Schedules a drain of the DOM update queue.
        /// </summary>
        private void ScheduleDomDrain()
        {
            if (this._domDrainScheduled || this._isClosing)
            {
                return;
            }

            this._domDrainScheduled = true;
            Task.Run(async () =>
            {
                await Task.Delay(DomDrainDebounceMs).ConfigureAwait(false);
                RhinoApp.InvokeOnUiThread(() =>
                {
                    Application.Instance?.AsyncInvoke(() => this.DrainDomUpdateQueue());
                });
            });
        }

        /// <summary>
        /// Drains the DOM update queue in batches.
        /// </summary>
        private void DrainDomUpdateQueue()
        {
            try
            {
                this._domDrainScheduled = false;
                if (this._isClosing) return;

                if (DateTime.UtcNow < this._deferDomUpdatesUntilUtc)
                {
                    // Still moving/resizing; try again shortly.
                    Task.Run(async () =>
                    {
                        await Task.Delay(DomDeferDuringMoveResizeMs).ConfigureAwait(false);
                        this.ScheduleDomDrain();
                    });
                    return;
                }

                if (this._isDomUpdating)
                {
                    // Another drain is already in progress; let it finish.
                    return;
                }

                this._isDomUpdating = true;
                try
                {
                    int executed = 0;
                    while (executed < DomDrainBatchSize && this._domUpdateQueue.Count > 0)
                    {
                        var next = this._domUpdateQueue.Dequeue();
                        try
                        {
                            next?.Invoke();
                        }
                        catch (Exception ex)
                        {
                            DebugLog($"[WebChatDialog] DOM queued action error: {ex.Message}");
                        }

                        executed++;
                    }
                }
                finally
                {
                    this._isDomUpdating = false;
                }

                // If there is more work, schedule another drain.
                if (this._domUpdateQueue.Count > 0)
                {
                    this.ScheduleDomDrain();
                }
            }
            catch (Exception ex)
            {
                DebugLog($"[WebChatDialog] DrainDomUpdateQueue error: {ex.Message}");
                this._isDomUpdating = false;
                this._domDrainScheduled = false;
            }
        }

        /// <summary>
        /// Executes JavaScript in the WebView on Rhino's UI thread.
        /// </summary>
        /// <param name="script">The JavaScript code to execute.</param>
        private void ExecuteScript(string script)
        {
            if (string.IsNullOrWhiteSpace(script) || this._isClosing)
            {
                return;
            }

            // If we are not currently draining the DOM queue, route this script through the queue.
            // This prevents WebView JS execution from occurring during window move/resize UI loops.
            if (!this._isDomUpdating)
            {
                this.RunWhenWebViewReady(() => this.ExecuteScript(script));
                return;
            }

            try
            {
                // Use AsyncInvoke to avoid running WebView/JS work inside other UI event handlers
                // (notably window move/resize), which can cause the UI to appear frozen.
                RhinoApp.InvokeOnUiThread(() =>
                {
                    Application.Instance?.AsyncInvoke(() =>
                    {
                        if (this._isClosing) return;
                        var entered = false;
                        try
                        {
                            // Enforce a small concurrency gate to avoid piling scripts into the WebView.
                            // Important: do NOT drop scripts (can truncate streamed UI updates). Re-queue when saturated.
                            var count = Interlocked.Increment(ref this._activeScripts);
                            if (count > MAX_CONCURRENT_SCRIPTS)
                            {
                                Interlocked.Decrement(ref this._activeScripts);
                                this.RunWhenWebViewReady(() => this.ExecuteScript(script));
                                return;
                            }

                            entered = true;
                            this._webView.ExecuteScript(script);
                        }
                        catch (Exception ex)
                        {
                            DebugLog($"[WebChatDialog] ExecuteScript error: {ex.Message}");
                        }
                        finally
                        {
                            if (entered)
                            {
                                Interlocked.Decrement(ref this._activeScripts);
                            }
                        }
                    });
                });
            }
            catch (Exception ex)
            {
                DebugLog($"[WebChatDialog] ExecuteScript marshal error: {ex.Message}");
            }
        }

        /// <summary>
        /// Adds a new interaction bubble to the WebView.
        /// </summary>
        /// <param name="interaction">The interaction to render and append.</param>
        private void AddInteractionToWebView(IAIInteraction interaction)
        {
            if (interaction == null)
            {
                return;
            }

            _ = this.QueueInteractionRenderAsync(null, interaction);
        }

        /// <summary>
        /// Upserts a message in the WebView using a stable DOM key. If a message with the same key exists,
        /// it is replaced; otherwise, it is appended. This ensures deterministic updates and prevents duplicates.
        /// </summary>
        /// <param name="domKey">The stable DOM key used to insert or replace the message.</param>
        /// <param name="interaction">The interaction to render and upsert.</param>
        /// <param name="source">Optional source identifier for logging and diagnostics.</param>
        private void UpsertMessageByKey(string domKey, IAIInteraction interaction, string? source = null, bool isStreaming = false)
        {
            if (interaction == null || string.IsNullOrWhiteSpace(domKey)) return;

            // Skip rendering empty assistant text bubbles (they're preserved in history but hidden from UI)
            if (interaction is AIInteractionText txt &&
                txt.Agent == AIAgent.Assistant &&
                string.IsNullOrWhiteSpace(txt.Content) &&
                string.IsNullOrWhiteSpace(txt.Reasoning))
            {
#if DEBUG
                DebugLog($"[WebChatDialog] UpsertMessageByKey (skipped empty assistant) key={domKey} src={source ?? "?"}");
#endif
                return;
            }

            _ = this.QueueInteractionRenderAsync(domKey, interaction, isStreaming: isStreaming);
        }

        /// <summary>
        /// Updates the idempotency cache with FIFO eviction to prevent unbounded growth.
        /// </summary>
        /// <param name="key">The DOM key.</param>
        /// <param name="html">The HTML content.</param>
        private void UpdateIdempotencyCache(string key, string html)
        {
            try
            {
                // If key already exists, we don't need to track it again in FIFO queue
                bool isExisting = this._lastDomHtmlByKey.ContainsKey(key);

                this._lastDomHtmlByKey[key] = html;

                if (!isExisting)
                {
                    this._idempotencyFifo.Enqueue(key);

                    // Evict oldest entry if cache exceeds max size
                    while (this._idempotencyFifo.Count > MaxIdempotencyCacheSize)
                    {
                        var oldest = this._idempotencyFifo.Dequeue();
                        this._lastDomHtmlByKey.Remove(oldest);
#if DEBUG
                        DebugLog($"[WebChatDialog] FIFO eviction: removed key={oldest} (cache size was {this._idempotencyFifo.Count + 1})");
#endif
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"[WebChatDialog] UpdateIdempotencyCache error: {ex.Message}");
            }
        }

        /// <summary>
        /// Marks the WebView as initialized only after the document is fully loaded.
        /// Ensures CoreWebView2 is ready before any ExecuteScript calls run.
        /// </summary>
        /// <param name="sender">The event source (WebView).</param>
        /// <param name="e">The load event arguments.</param>
        private void WebView_DocumentLoaded(object? sender, WebViewLoadedEventArgs e)
        {
            try
            {
                RhinoApp.InvokeOnUiThread(() =>
                {
                    this._webViewInitialized = true;
                    try
                    {
                        this._webViewInitializedTcs.TrySetResult(true);
                    }
                    catch
                    {
                    }

                    // On a fresh document load, clear our idempotency cache
                    try
                    {
                        this._lastDomHtmlByKey.Clear();
                        this._idempotencyFifo.Clear();
                    }
                    catch
                    {
                    }

                    // Reflect status in web UI
                    try
                    {
                        this.ExecuteScript($"setStatus({JsonConvert.SerializeObject(this._pendingStatusAfter ?? "Ready")}); setProcessing(false);");
                    }
                    catch
                    {
                    }
                });
            }
            catch (Exception ex)
            {
                DebugLog($"[WebChatDialog] WebView_DocumentLoaded error: {ex.Message}");
            }
        }

        /// <summary>
        /// Adds a system message to the WebView.
        /// </summary>
        /// <param name="text">The system text content to display.</param>
        /// <param name="level">An optional severity level (e.g., info, warning, error).</param>
        private void AddSystemMessage(string text, string level = "info")
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var msg = new AIInteractionText { Agent = AIAgent.System, Content = text };
            if (msg is IAIKeyedInteraction keyed)
            {
                this.UpsertMessageByKey(keyed.GetDedupKey(), msg);
            }
            else
            {
                this.AddInteractionToWebView(msg);
            }
        }

        /// <summary>
        /// Emits a snapshot of the current conversation state via ChatUpdated.
        /// </summary>
        private void BuildAndEmitSnapshot()
        {
            try
            {
                var snapshot = this._currentSession != null
                    ? this._currentSession.GetHistoryReturn()
                    : new AIReturn { SkipRequestValidation = true, SkipMetricsValidation = true };
                this.ChatUpdated?.Invoke(this, snapshot);
            }
            catch (Exception ex)
            {
                DebugLog($"[WebChatDialog] BuildAndEmitSnapshot error: {ex.Message}");
            }
        }

        /// <summary>
        /// Replays the entire conversation history from the current session into the WebView.
        /// Preserves the interaction order as stored in the session history.
        /// </summary>
        private void ReplayFullHistoryToWebView()
        {
            try
            {
                if (!this._webViewInitialized)
                {
                    this.RunWhenWebViewReady(() => this.ReplayFullHistoryToWebView());
                    return;
                }

                var interactions = this._currentSession?.GetHistoryInteractionList();
                if (interactions == null || interactions.Count == 0)
                {
                    return;
                }

                // During a full replay (including Regen), maintain strict DOM order by chaining inserts.
                // Rationale: Upsert-by-key alone can lead to out-of-order layout if rendering is deferred per message.
                this.RunWhenWebViewReady(() =>
                {
                    string? prevKey = null;

                    foreach (var interaction in interactions)
                    {
                        if (interaction is not IAIKeyedInteraction keyed)
                        {
                            this.AddSystemMessage($"Could not render interaction during history replay: missing dedupKey (type={interaction?.GetType().Name}, agent={interaction?.Agent})", "error");
                            prevKey = null;
                            continue;
                        }

                        var key = keyed.GetDedupKey();
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            this.AddSystemMessage($"Could not render interaction during history replay: missing dedupKey (type={interaction?.GetType().Name}, agent={interaction?.Agent})", "error");
                            prevKey = null;
                            continue;
                        }

                        string html;
                        try
                        {
                            lock (this._htmlRenderLock)
                            {
                                html = this._htmlRenderer.RenderInteraction(interaction);
                            }
                        }
                        catch (Exception ex)
                        {
                            this.AddSystemMessage($"Could not render interaction during history replay: {ex.Message}", "error");
                            prevKey = null;
                            continue;
                        }

                        if (string.IsNullOrEmpty(html)) continue;

                        // Keep host-side idempotency cache in sync
                        try
                        {
                            this.UpdateIdempotencyCache(key, html ?? string.Empty);
                        }
                        catch (Exception ex)
                        {
                            DebugLog($"[WebChatDialog] Error updating idempotency cache: {ex.Message}");
                        }

                        if (!string.IsNullOrWhiteSpace(prevKey))
                        {
                            this.ExecuteScript($"upsertMessageAfter({JsonConvert.SerializeObject(prevKey)}, {JsonConvert.SerializeObject(key)}, {JsonConvert.SerializeObject(html)});");
                        }
                        else
                        {
                            this.ExecuteScript($"upsertMessage({JsonConvert.SerializeObject(key)}, {JsonConvert.SerializeObject(html)});");
                        }

                        prevKey = key;
                    }
                });
            }
            catch (Exception ex)
            {
                DebugLog($"[WebChatDialog] ReplayFullHistoryToWebView error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles sending a user message from the input box.
        /// </summary>
        // No no-arg SendMessage: messages come from WebView events and call SendMessage(string)

        /// <summary>
        /// Event raised when a new AI response is received.
        /// </summary>
        public event EventHandler<AIReturn>? ResponseReceived;

        /// <summary>
        /// Event raised whenever the chat state is updated (partial streams, tool events, user messages, or final result).
        /// Carries a snapshot AIReturn reflecting the current conversation state.
        /// </summary>
        public event EventHandler<AIReturn>? ChatUpdated;

        /// <summary>
        /// Gets the last AI return received from the chat dialog.
        /// </summary>
        /// <returns>The most recent AIReturn produced by the current conversation session; a new empty AIReturn if none.</returns>
        public AIReturn GetLastReturn() => this._currentSession?.LastReturn
            ?? new AIReturn { SkipRequestValidation = true, SkipMetricsValidation = true };

        /// <summary>
        /// Gets the combined metrics from interactions in the conversation.
        /// </summary>
        /// <param name="newInteractionsOnly">When true, returns metrics from new interactions only; when false, returns metrics from all history.</param>
        /// <returns>Combined AI metrics from conversation interactions.</returns>
        public AIMetrics GetCombinedMetrics(bool newInteractionsOnly = false)
        {
            return this._currentSession?.GetCombinedMetrics(newInteractionsOnly) ?? new AIMetrics();
        }

        /// <summary>
        /// Loads the initial chat HTML into the WebView and updates UI status/progress safely on Rhino's UI thread.
        /// </summary>
        /// <param name="showProgress">Whether to show the progress bar during load.</param>
        /// <param name="setWebViewInitialized">Whether to set the WebView as initialized and complete the TCS.</param>
        /// <param name="statusBefore">Optional status text to set before loading. Pass null to skip.</param>
        /// <param name="statusAfter">Status text to set after loading completes.</param>
        private void LoadInitialHtmlIntoWebView(bool showProgress, bool setWebViewInitialized, string statusBefore = "Loading chat UI...", string statusAfter = "Ready")
        {
            var html = this._htmlRenderer.GetInitialHtml();

            // Each time we (re)load HTML, reset readiness and TCS; readiness will be set on DocumentLoaded
            this._webViewInitialized = false;
            this._webViewInitializedTcs = new TaskCompletionSource<bool>();
            this._pendingStatusAfter = statusAfter ?? "Ready";

            RhinoApp.InvokeOnUiThread(() =>
            {
                try
                {
                    // Load the HTML into the WebView.
                    // On macOS, Eto.Forms WKWebViewHandler.LoadHtml() calls WKWebView.LoadFileUrl()
                    // which only accepts file:// URIs. Pass null on non-Windows to use about:blank origin;
                    // the JS-to-C# bridge uses WKUserContentController message handlers, not origin.
                    Uri baseUri = IsWindows
                        ? new Uri("https://smarthopper.local/")
                        : null;
                    this._webView.LoadHtml(html, baseUri);

                    // Do not mark initialized here; wait for DocumentLoaded to ensure CoreWebView2 is ready
                }
                catch (Exception ex)
                {
                    DebugLog($"[WebChatDialog] LoadInitialHtmlIntoWebView UI error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Initializes the WebView with the initial HTML and starts optional greeting.
        /// </summary>
        private async Task InitializeWebViewAsync()
        {
            try
            {
                this.LoadInitialHtmlIntoWebView(showProgress: true, setWebViewInitialized: true);

                // Once the WebView is ready, replay full conversation history to ensure fidelity
                this.RunWhenWebViewReady(() =>
                {
                    try
                    {
                        this.ReplayFullHistoryToWebView();
                        this.ExecuteScript("setStatus('Ready'); setProcessing(false);");
                        this._initialHistoryReplayTcs.TrySetResult(true);
                    }
                    catch (Exception rex)
                    {
                        DebugLog($"[WebChatDialog] InitializeWebViewAsync replay error: {rex.Message}");
                        this._initialHistoryReplayTcs.TrySetResult(false);
                    }
                });

                await this.InitializeNewConversationAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DebugLog($"[WebChatDialog] InitializeWebViewAsync error: {ex.Message}");
                try
                {
                    this._webViewInitializedTcs.TrySetException(ex);
                }
                catch (Exception rex)
                {
                    DebugLog($"[WebChatDialog] Failed to set exception on WebView initialized TCS: {rex.Message}");
                }
            }
        }

        /// <summary>
        /// Processes an AI interaction using the new AICall models and handles tool calls automatically.
        /// </summary>
        private async Task ProcessAIInteraction()
        {
            try
            {
                DebugLog("[WebChatDialog] Processing AI interaction with existing session reuse");

                // Enter processing state: disable input/send, enable cancel in the web UI
                this.RunWhenWebViewReady(() => this.ExecuteScript("setProcessing(true);"));

                // Observer already attached at construction time
                this._currentCts = new CancellationTokenSource();

                // Reuse existing session if available, otherwise create new one
                if (this._currentSession == null)
                {
                    throw new Exception("[WebChatDialog] No existing conversation session found");
                }
                else
                {
                    DebugLog("[WebChatDialog] Reusing existing ConversationSession");
                }

                // Add the pending user message to the session
                if (!string.IsNullOrWhiteSpace(this._pendingUserMessage))
                {
                    this._currentSession.AddInteraction(this._pendingUserMessage);
                    this._pendingUserMessage = null; // Clear after adding
                }

                var options = new SessionOptions { ProcessTools = true, CancellationToken = this._currentCts.Token };

                // Always attempt streaming first - ConversationSession handles validation internally
                // and falls back to non-streaming if streaming is not supported
                DebugLog("[WebChatDialog] Starting streaming path (session handles validation)");
                var streamingOptions = new StreamingOptions();

                AIReturn? lastStreamReturn = null;
                await foreach (var r in this._currentSession
                    .Stream(options, streamingOptions, this._currentCts.Token)
                    .ConfigureAwait(false))
                {
                    lastStreamReturn = r;

                    // Observer handles partial/final UI updates
                }

                // Check if streaming returned a validation error (provider/model doesn't support streaming)
                // In that case, ConversationSession already handles fallback internally via OnFinal
                bool hasValidationError = lastStreamReturn?.Messages?.Any(m =>
                    m != null &&
                    m.Severity == SHRuntimeMessageSeverity.Error &&
                    m.Origin == SHRuntimeMessageOrigin.Validation) ?? false;

                // If we got a validation error with no content, fall back to non-streaming
                bool hasContent = lastStreamReturn?.Body?.Interactions?.Any(i =>
                    i is AIInteractionText t && !string.IsNullOrWhiteSpace(t.Content)) ?? false;

                if (hasValidationError && !hasContent)
                {
                    DebugLog("[WebChatDialog] Streaming validation failed. Falling back to non-streaming path");
                    await this._currentSession.RunToStableResult(options).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                DebugLog($"[WebChatDialog] Error in ProcessAIInteraction: {ex.Message}");
                try
                {
                    this.AddSystemMessage($"Error: {ex.Message}", "error");
                    _ = this.FinishRenderingAsync(() => this.ExecuteScript("setStatus('Error'); setProcessing(false);"));
                    this.BuildAndEmitSnapshot();
                }
                catch (Exception rex)
                {
                    DebugLog($"[WebChatDialog] Error in ProcessAIInteraction: {rex.Message}");
                }
            }
            finally
            {
                try
                {
                    this._currentCts?.Cancel();
                }
                catch (Exception rex)
                {
                    DebugLog($"[WebChatDialog] Error in ProcessAIInteraction: {rex.Message}");
                }

                this._currentCts?.Dispose();
                this._currentCts = null;

                // Leave processing state: re-enable input/send, disable cancel in the web UI
                _ = this.FinishRenderingAsync(() => this.ExecuteScript("setProcessing(false);"));

                // Keep the session alive for reuse - do not set to null
            }
        }

        /// <summary>
        /// Initializes a new conversation and, if requested, triggers a one-shot provider run to emit the greeting.
        /// </summary>
        private async Task InitializeNewConversationAsync()
        {
            // For fidelity, history is fully replayed elsewhere. Keep this method minimal to maintain compatibility.
            try
            {
                this.RunWhenWebViewReady(() => this.ExecuteScript("setStatus('Ready'); setProcessing(false);"));

                try
                {
                    await this._initialHistoryReplayTcs.Task.ConfigureAwait(false);
                }
                catch (Exception rex)
                {
                    DebugLog($"[WebChatDialog] Error in InitializeNewConversationAsync: {rex.Message}");
                }

                // If greeting was requested by the creator (e.g., CanvasButton), run a single non-streaming turn.
                if (this._generateGreeting && this._currentSession != null)
                {
                    try
                    {
                        var options = new SessionOptions { ProcessTools = false, MaxTurns = 1 };
                        await this._currentSession.RunToStableResult(options).ConfigureAwait(false);
                    }
                    catch (Exception grex)
                    {
                        DebugLog($"[WebChatDialog] Greeting init error: {grex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"[WebChatDialog] Error in InitializeNewConversation: {ex.Message}");
            }
        }

        /// <summary>
        /// Intercepts navigation events from the WebView to handle sh:// commands from JS.
        /// </summary>
        /// <param name="sender">The event source (WebView).</param>
        /// <param name="e">The navigation event arguments, used to inspect and cancel navigation.</param>
        private void WebView_DocumentLoading(object? sender, WebViewLoadingEventArgs e)
        {
            try
            {
                DebugLog($"[WebChatDialog] WebView_DocumentLoading called");
                if (e?.Uri is not Uri uri)
                {
                    DebugLog($"[WebChatDialog] Navigation URI is null");
                    return;
                }

                DebugLog($"[WebChatDialog] Navigation URI: {uri} (scheme: {uri.Scheme})");

                if (uri.Scheme.Equals("sh", StringComparison.OrdinalIgnoreCase))
                {
                    DebugLog($"[WebChatDialog] Intercepting sh:// scheme, cancelling navigation");
                    e.Cancel = true;
                    var query = ParseQueryString(uri.Query);
                    var type = (query.TryGetValue("type", out var t) ? t : string.Empty).ToLowerInvariant();
                    DebugLog($"[WebChatDialog] sh:// event type: '{type}', query params: {string.Join(", ", query.Keys)}");

                    switch (type)
                    {
                        case "send":
                            {
                                var text = query.TryGetValue("text", out var txt) ? txt : string.Empty;
                                DebugLog($"[WebChatDialog] Handling send event, text length: {text.Length}");

                                // Defer to next UI tick to avoid executing scripts during navigation event
                                Application.Instance?.AsyncInvoke(() =>
                                {
                                    try
                                    {
                                        this.SendMessage(text);
                                    }
                                    catch (Exception ex)
                                    {
                                        DebugLog($"[WebChatDialog] Deferred SendMessage error: {ex.Message}");
                                    }
                                });
                                break;
                            }

                        case "cancel":
                            DebugLog($"[WebChatDialog] Handling cancel event");

                            // Defer to next UI tick to avoid executing scripts during navigation event
                            Application.Instance?.AsyncInvoke(() =>
                            {
                                try
                                {
                                    this.CancelChat();
                                }
                                catch (Exception ex)
                                {
                                    DebugLog($"[WebChatDialog] Deferred CancelChat error: {ex.Message}");
                                }
                            });
                            break;

#if DEBUG
                        case "regen":
                            DebugLog($"[WebChatDialog] Handling regen event");

                            // Defer to next UI tick to avoid executing scripts during navigation event
                            Application.Instance?.AsyncInvoke(() =>
                            {
                                try
                                {
                                    this.RegenChat();
                                }
                                catch (Exception ex)
                                {
                                    DebugLog($"[WebChatDialog] Deferred RegenChat error: {ex.Message}");
                                }
                            });
                            break;

                        case "update":
                            DebugLog($"[WebChatDialog] Handling update event");

                            // Defer to next UI tick to avoid executing scripts during navigation event
                            Application.Instance?.AsyncInvoke(() =>
                            {
                                try
                                {
                                    this.UpdateChatView();
                                }
                                catch (Exception ex)
                                {
                                    DebugLog($"[WebChatDialog] Deferred UpdateChatView error: {ex.Message}");
                                }
                            });
                            break;
#endif

                        default:
                            DebugLog($"[WebChatDialog] Unknown sh:// event type: '{type}'");
                            break;
                    }
                }
                else if (uri.Scheme.Equals("clipboard", StringComparison.OrdinalIgnoreCase))
                {
                    DebugLog($"[WebChatDialog] Intercepting clipboard:// scheme");

                    // Handle copy-to-clipboard from JS
                    e.Cancel = true;
                    var query = ParseQueryString(uri.Query);
                    var text = query.TryGetValue("text", out var t) ? t : string.Empty;
                    DebugLog($"[WebChatDialog] Clipboard text length: {text.Length}");
                    try
                    {
                        RhinoApp.InvokeOnUiThread(() =>
                        {
                            try
                            {
                                var cb = new Clipboard();
                                cb.Text = text;
                                DebugLog($"[WebChatDialog] Text copied to clipboard successfully");
                            }
                            catch (Exception ex)
                            {
                                DebugLog($"[WebChatDialog] Clipboard set failed: {ex.Message}");
                            }
                        });
                        this.RunWhenWebViewReady(() => this.ExecuteScript("showToast('Copied to clipboard');"));
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"[WebChatDialog] Clipboard handling error: {ex.Message}");
                    }
                }
                else
                {
                    DebugLog($"[WebChatDialog] Allowing normal navigation to: {uri}");
                }
            }
            catch (Exception ex)
            {
                DebugLog($"[WebChatDialog] WebView_DocumentLoading error: {ex.Message}");
            }
        }

        private void CancelChat()
        {
            try
            {
                this._currentCts?.Cancel();
                this._currentSession?.Cancel();
                DebugLog("[WebChatDialog] Cancellation requested");
            }
            catch (Exception ex)
            {
                DebugLog($"[WebChatDialog] Error requesting cancellation: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles dialog closing by cancelling any ongoing AI interactions.
        /// </summary>
        private void OnDialogClosing()
        {
            try
            {
                DebugLog("[WebChatDialog] Dialog closing, cancelling ongoing AI calls");
                this._isClosing = true;
                this._renderScheduler.Dispose();
                this._domUpdateQueue.Clear();
                this._webViewInitializedTcs.TrySetCanceled();
                this.CancelChat();
                if (this._currentSession is IDisposable disposableSession)
                {
                    disposableSession.Dispose();
                }
            }
            catch (Exception ex)
            {
                DebugLog($"[WebChatDialog] Error during dialog closing: {ex.Message}");
            }
        }

#if DEBUG
        private void RegenChat()
        {
            try
            {
                this.CancelChat();

                this.RunWhenWebViewReady(() =>
                {
                    try
                    {
                        this.ExecuteScript("resetMessages(); setStatus('Ready'); setProcessing(false);");

                        try
                        {
                            this._lastDomHtmlByKey.Clear();
                            this._idempotencyFifo.Clear();
                            this._renderScheduler.Reset();
                        }
                        catch
                        {
                        }

                        this.ReplayFullHistoryToWebView();
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"[WebChatDialog] RegenChat UI error: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                DebugLog($"[WebChatDialog] RegenChat error: {ex.Message}");
            }
        }

        private void UpdateChatView()
        {
            try
            {
                this.RunWhenWebViewReady(() =>
                {
                    try
                    {
                        // Get all message keys currently in the DOM
                        // NOTE: WebView.ExecuteScript return format varies by platform/implementation.
                        // It may return:
                        // - a raw JSON array (e.g. ["k1","k2"])
                        // - a quoted JSON string (e.g. "[\"k1\",\"k2\"]")
                        // - "undefined" / "null" / empty
                        string rawDomKeysResult = this._webView.ExecuteScript("getAllMessageKeys();") ?? string.Empty;

                        // Normalize to a JSON array string
                        string domKeysJson = rawDomKeysResult.Trim();
                        if (string.Equals(domKeysJson, "undefined", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(domKeysJson, "null", StringComparison.OrdinalIgnoreCase))
                        {
                            domKeysJson = string.Empty;
                        }

                        // If WebView returned a quoted JSON string, unquote it first
                        if (!string.IsNullOrWhiteSpace(domKeysJson) && domKeysJson.StartsWith("\"", StringComparison.Ordinal))
                        {
                            try
                            {
                                domKeysJson = JsonConvert.DeserializeObject<string>(domKeysJson) ?? string.Empty;
                            }
                            catch
                            {
                                // Fall through with original value
                            }
                        }

                        // Final fallback: ask JS to stringify explicitly
                        if (string.IsNullOrWhiteSpace(domKeysJson) || (!domKeysJson.StartsWith("[", StringComparison.Ordinal) && !domKeysJson.StartsWith("{", StringComparison.Ordinal)))
                        {
                            domKeysJson = (this._webView.ExecuteScript("JSON.stringify(getAllMessageKeys());") ?? string.Empty).Trim();
                            if (!string.IsNullOrWhiteSpace(domKeysJson) && domKeysJson.StartsWith("\"", StringComparison.Ordinal))
                            {
                                try
                                {
                                    domKeysJson = JsonConvert.DeserializeObject<string>(domKeysJson) ?? string.Empty;
                                }
                                catch
                                {
                                }
                            }
                        }

                        if (string.IsNullOrWhiteSpace(domKeysJson))
                        {
                            DebugLog($"[WebChatDialog] UpdateChatView: No DOM keys returned (raw='{rawDomKeysResult}')");
                            return;
                        }

                        List<string>? domKeys;
                        try
                        {
                            domKeys = JsonConvert.DeserializeObject<List<string>>(domKeysJson);
                        }
                        catch (Exception dex)
                        {
                            DebugLog($"[WebChatDialog] UpdateChatView: Failed to parse DOM keys. raw='{rawDomKeysResult}', normalized='{domKeysJson}'. Error: {dex.Message}");
                            this.ExecuteScript("showToast('Update failed: could not parse DOM keys');");
                            return;
                        }

                        if (domKeys == null || domKeys.Count == 0)
                        {
                            DebugLog("[WebChatDialog] UpdateChatView: No messages in DOM");
                            return;
                        }

                        DebugLog($"[WebChatDialog] UpdateChatView: Found {domKeys.Count} messages in DOM");

                        // Get conversation history
                        var interactions = this._currentSession?.GetHistoryInteractionList();
                        if (interactions == null || interactions.Count == 0)
                        {
                            DebugLog("[WebChatDialog] UpdateChatView: No conversation history");
                            return;
                        }

                        // Build a lookup map: dedupKey -> interaction
                        var interactionMap = new Dictionary<string, IAIInteraction>(StringComparer.Ordinal);
                        foreach (var interaction in interactions)
                        {
                            if (interaction is IAIKeyedInteraction keyed)
                            {
                                var key = keyed.GetDedupKey();
                                if (!string.IsNullOrWhiteSpace(key))
                                {
                                    interactionMap[key] = interaction;
                                }
                            }
                        }

                        // Check each DOM message and update if hash differs
                        int updatedCount = 0;
                        int skippedCount = 0;

                        foreach (var domKey in domKeys)
                        {
                            if (!interactionMap.TryGetValue(domKey, out var interaction))
                            {
                                DebugLog($"[WebChatDialog] UpdateChatView: DOM key '{domKey}' not found in history, skipping");
                                skippedCount++;
                                continue;
                            }

                            // Render the current state of this interaction
                            string html;
                            try
                            {
                                lock (this._htmlRenderLock)
                                {
                                    html = this._htmlRenderer.RenderInteraction(interaction);
                                }
                            }
                            catch (Exception ex)
                            {
                                DebugLog($"[WebChatDialog] UpdateChatView: Failed to render interaction '{domKey}': {ex.Message}");
                                skippedCount++;
                                continue;
                            }

                            if (string.IsNullOrEmpty(html)) continue;

                            // Compare hash with cached version
                            if (this._lastDomHtmlByKey.TryGetValue(domKey, out var cachedHtml))
                            {
                                if (string.Equals(html, cachedHtml, StringComparison.Ordinal))
                                {
                                    // Same content, skip update
                                    skippedCount++;
                                    continue;
                                }
                            }

                            // Content differs, update the message
                            this.UpdateIdempotencyCache(domKey, html ?? string.Empty);
                            this.ExecuteScript($"upsertMessage({JsonConvert.SerializeObject(domKey)}, {JsonConvert.SerializeObject(html)});");
                            updatedCount++;
                        }

                        var statusMsg = $"Update complete: {updatedCount} updated, {skippedCount} unchanged";
                        DebugLog($"[WebChatDialog] UpdateChatView: {statusMsg}");
                        this.ExecuteScript($"showToast({JsonConvert.SerializeObject(statusMsg)});");
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"[WebChatDialog] UpdateChatView UI error: {ex.Message}");
                        this.ExecuteScript($"showToast('Update failed: {ex.Message.Replace("'", "\\'")}');");
                    }
                });
            }
            catch (Exception ex)
            {
                DebugLog($"[WebChatDialog] UpdateChatView error: {ex.Message}");
            }
        }

#endif

        /// <summary>
        /// Handles a user message submitted from the WebView.
        /// </summary>
        private void SendMessage(string text)
        {
            try
            {
                DebugLog($"[WebChatDialog] SendMessage called with text length: {text?.Length ?? 0}");
                if (string.IsNullOrWhiteSpace(text))
                {
                    DebugLog($"[WebChatDialog] SendMessage: text is null or whitespace, returning");
                    return;
                }

                var trimmed = text.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) return;

                // Store the user message before processing
                // The observer will render it when AddInteraction() is called on the session
                this._pendingUserMessage = trimmed;

                // Immediately reflect processing state in UI to disable input/send and enable cancel
                this.RunWhenWebViewReady(() => this.ExecuteScript("setProcessing(true);"));

                // Kick off processing asynchronously
                DebugLog("[WebChatDialog] Scheduling ProcessAIInteraction task");
                Task.Run(async () =>
                {
                    try
                    {
                        DebugLog("[WebChatDialog] ProcessAIInteraction task starting");
                        await this.ProcessAIInteraction().ConfigureAwait(false);
                        DebugLog("[WebChatDialog] ProcessAIInteraction task finished");
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"[WebChatDialog] ProcessAIInteraction task error: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                DebugLog($"[WebChatDialog] SendMessage(text) error: {ex.Message}");
            }
        }

    }
}
