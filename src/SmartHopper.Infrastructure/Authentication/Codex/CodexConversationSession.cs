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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SmartHopper.Infrastructure.AICall.Core.Base;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.Core.Requests;
using SmartHopper.Infrastructure.AICall.Core.Returns;
using SmartHopper.Infrastructure.AICall.Metrics;
using SmartHopper.Infrastructure.AICall.Sessions;
using SmartHopper.Infrastructure.AIContext;
using SmartHopper.Infrastructure.Streaming;

namespace SmartHopper.Infrastructure.Authentication.Codex
{
    /// <summary>
    /// Adapts Codex app-server threads and turns to SmartHopper's chat session contract.
    /// </summary>
    public sealed class CodexConversationSession : IConversationSession, ICodexConfigurableConversationSession, ICodexCanvasToolSession, IDisposable
    {
        private readonly ICodexConversationTransport transport;
        private readonly IConversationObserver observer;
        private readonly string systemPrompt;
        private readonly bool includeCurrentContext;
        private readonly ICodexDynamicToolBridge? dynamicToolBridge;
        private readonly CodexServerRequestRouter? requestRouter;
        private readonly SemaphoreSlim turnGate = new (1, 1);
        private readonly ConcurrentDictionary<string, byte> handledCallIds = new (StringComparer.Ordinal);
        private string model;
        private string reasoningEffort = string.Empty;
        private readonly object sync = new ();
        private readonly List<IAIInteraction> history = new ();
        private TaskCompletionSource<JObject>? turnCompleted;
        private TaskCompletionSource<string>? turnStarted;
        private readonly List<CodexNotificationEventArgs> pendingTurnNotifications = new ();
        private StringBuilder streamedText = new ();
        private string? threadId;
        private string? activeTurnId;
        private string? pendingUserMessage;
        private string pendingDocumentContext = string.Empty;
        private CancellationTokenSource? activeToolCancellation;
        private AIReturn lastReturn = CreateReturn(Array.Empty<IAIInteraction>());
        private bool dynamicToolsFallbackReported;
        private bool processToolsForTurn = true;
        private long maxTokensForTurn;
        private long? reportedTotalTokens;
        private long? tokenBaselineForTurn;
        private int disposed;

        public CodexConversationSession(
            ICodexConversationTransport transport,
            string model,
            string systemPrompt,
            IConversationObserver observer,
            bool includeCurrentContext = true,
            ICodexDynamicToolBridge? dynamicToolBridge = null)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.model = model ?? string.Empty;
            this.systemPrompt = systemPrompt ?? string.Empty;
            this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
            this.includeCurrentContext = includeCurrentContext;
            this.dynamicToolBridge = dynamicToolBridge;
            this.Request = new AIRequestCall
            {
                Provider = "Codex Account",
                Model = this.model,
                Endpoint = "canvas-chat",
                Capability = AIModels.AICapability.None,
            };
            this.transport.NotificationReceived += this.OnNotificationReceived;
            if (this.dynamicToolBridge != null)
            {
                this.requestRouter = CodexServerRequestRouter.Acquire(this.transport);
            }
        }

        public AIRequestCall Request { get; }

        public AIReturn LastReturn => this.lastReturn;

        public ICodexCanvasToolBridge? CanvasTools => this.dynamicToolBridge as ICodexCanvasToolBridge;

        public string SelectedModel
        {
            get
            {
                lock (this.sync)
                {
                    return this.model;
                }
            }
        }

        public string SelectedReasoningEffort
        {
            get
            {
                lock (this.sync)
                {
                    return this.reasoningEffort;
                }
            }
        }

        public void Configure(string? model, string? reasoningEffort)
        {
            this.ThrowIfDisposed();
            lock (this.sync)
            {
                this.model = model?.Trim() ?? string.Empty;
                this.reasoningEffort = reasoningEffort?.Trim() ?? string.Empty;
                this.Request.Model = this.model;
            }
        }

        public async Task<IReadOnlyList<CodexModelOption>> GetModelsAsync(CancellationToken cancellationToken = default)
        {
            this.ThrowIfDisposed();
            await this.transport.EnsureSignedInAsync(cancellationToken).ConfigureAwait(false);

            var models = new List<CodexModelOption>();
            string? cursor = null;
            do
            {
                var response = await this.transport.SendAsync(
                    "model/list",
                    new JObject
                    {
                        ["cursor"] = cursor,
                        ["includeHidden"] = false,
                    },
                    TimeSpan.FromSeconds(30),
                    cancellationToken).ConfigureAwait(false);

                var payload = response as JObject;
                foreach (var item in payload?["data"] as JArray ?? new JArray())
                {
                    if (item is not JObject modelItem || modelItem.Value<bool?>("hidden") == true)
                    {
                        continue;
                    }

                    var modelValue = modelItem.Value<string>("model") ?? modelItem.Value<string>("id") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(modelValue))
                    {
                        continue;
                    }

                    var efforts = new List<CodexReasoningEffortOption>();
                    foreach (var effortItem in modelItem["supportedReasoningEfforts"] as JArray ?? new JArray())
                    {
                        var value = effortItem.Value<string>("reasoningEffort") ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            efforts.Add(new CodexReasoningEffortOption
                            {
                                Value = value,
                                Description = effortItem.Value<string>("description") ?? string.Empty,
                            });
                        }
                    }

                    models.Add(new CodexModelOption
                    {
                        Model = modelValue,
                        DisplayName = modelItem.Value<string>("displayName") ?? modelValue,
                        Description = modelItem.Value<string>("description") ?? string.Empty,
                        IsDefault = modelItem.Value<bool?>("isDefault") == true,
                        DefaultReasoningEffort = modelItem.Value<string>("defaultReasoningEffort") ?? string.Empty,
                        SupportedReasoningEfforts = efforts,
                    });
                }

                cursor = payload?.Value<string>("nextCursor");
            }
            while (!string.IsNullOrWhiteSpace(cursor));

            return models;
        }

        public void AddInteraction(string userMessage)
        {
            this.ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(userMessage))
            {
                return;
            }

            var interaction = new AIInteractionText
            {
                Agent = AIAgent.User,
                Content = userMessage,
                TurnId = Guid.NewGuid().ToString("N"),
            };
            lock (this.sync)
            {
                this.history.Add(interaction);
                this.pendingUserMessage = userMessage;
                this.UpdateLastReturn();
            }

            this.observer.OnInteractionCompleted(interaction);
        }

        public AIReturn GetHistoryReturn()
        {
            lock (this.sync)
            {
                return CreateReturn(this.history);
            }
        }

        public void AddInteraction(IAIInteraction interaction)
        {
            if (interaction == null) return;
            lock (this.sync)
            {
                this.history.Add(interaction);
                if (interaction is AIInteractionDocumentEvent observation)
                    this.pendingDocumentContext += "\n[Canvas observation] " + observation.Content;
                this.UpdateLastReturn();
            }
            this.observer.OnInteractionCompleted(interaction);
        }

        public List<IAIInteraction> GetHistoryInteractionList()
        {
            lock (this.sync)
            {
                return this.history.ToList();
            }
        }

        public AIMetrics GetCombinedMetrics(bool newInteractionsOnly = false)
        {
            string currentModel;
            lock (this.sync)
            {
                currentModel = this.model;
            }

            return new AIMetrics
            {
                Provider = "Codex Account",
                Model = currentModel,
            };
        }

        public AIMetrics GetTurnMetrics(string turnId)
        {
            return this.GetCombinedMetrics();
        }

        public async Task<AIReturn> RunToStableResult(SessionOptions options, CancellationToken cancellationToken = default)
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken, cancellationToken);
            if (options.MaxRunSeconds > 0) linkedCancellation.CancelAfter(TimeSpan.FromSeconds(options.MaxRunSeconds));
            return await this.RunTurnAsync(options.ProcessTools, linkedCancellation.Token, options.MaxRunTokens).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<AIReturn> Stream(
            SessionOptions options,
            StreamingOptions streamingOptions,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken, cancellationToken);
            if (options.MaxRunSeconds > 0) linkedCancellation.CancelAfter(TimeSpan.FromSeconds(options.MaxRunSeconds));
            yield return await this.RunTurnAsync(options.ProcessTools, linkedCancellation.Token, options.MaxRunTokens).ConfigureAwait(false);
        }

        public void Cancel()
        {
            string? currentThread;
            string? currentTurn;
            lock (this.sync)
            {
                currentThread = this.threadId;
                currentTurn = this.activeTurnId;
                // Keep cancellation bound to this turn until the bridge has also been notified.
                CancelTools(this.activeToolCancellation);
                this.dynamicToolBridge?.CancelPending();
            }

            if (!string.IsNullOrWhiteSpace(currentThread) && !string.IsNullOrWhiteSpace(currentTurn))
            {
                _ = this.InterruptTurnAsync(
                    currentThread,
                    currentTurn);
            }
        }

        private async Task InterruptTurnAsync(string currentThread, string currentTurn)
        {
            try
            {
                await this.transport.SendAsync(
                    "turn/interrupt",
                    new JObject
                    {
                        ["threadId"] = currentThread,
                        ["turnId"] = currentTurn,
                    },
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cancellation must still complete locally when the transport has already closed.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            {
                return;
            }

            this.transport.NotificationReceived -= this.OnNotificationReceived;
            lock (this.sync)
            {
                this.requestRouter?.Release(this.threadId);
                CancelTools(this.activeToolCancellation);
                this.turnCompleted?.TrySetCanceled();
            }

            this.dynamicToolBridge?.Dispose();
        }

        private async Task<AIReturn> RunTurnAsync(bool processTools, CancellationToken cancellationToken, long maxTokens = 0)
        {
            await this.turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            this.maxTokensForTurn = maxTokens;
            this.tokenBaselineForTurn = this.reportedTotalTokens;
            try
            {
                lock (this.sync)
                {
                    this.processToolsForTurn = processTools;
                }

                return await this.RunTurnCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                this.turnGate.Release();
            }
        }

        private async Task<AIReturn> RunTurnCoreAsync(CancellationToken cancellationToken)
        {
            this.ThrowIfDisposed();
            string? userMessage;
            lock (this.sync)
            {
                userMessage = this.pendingUserMessage;
                this.pendingUserMessage = null;
                if (!string.IsNullOrWhiteSpace(userMessage))
                {
                    userMessage = this.pendingDocumentContext + "\n\n" + userMessage;
                    this.pendingDocumentContext = string.Empty;
                }
            }

            if (string.IsNullOrWhiteSpace(userMessage))
            {
                return this.GetHistoryReturn();
            }

            using var toolCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (this.sync)
            {
                this.ThrowIfDisposed();
                this.turnCompleted = completion;
                this.turnStarted = started;
                this.streamedText = new StringBuilder();
                this.activeTurnId = null;
                this.activeToolCancellation = toolCancellation;
            }

            using var cancellationRegistration = cancellationToken.Register(this.Cancel);
            try
            {
                this.observer.OnStart(this.Request);
                await this.EnsureThreadAsync(toolCancellation.Token).ConfigureAwait(false);
                var prompt = this.includeCurrentContext ? AddCurrentContext(userMessage) : userMessage;
                string currentModel;
                string currentReasoningEffort;
                lock (this.sync)
                {
                    currentModel = this.model;
                    currentReasoningEffort = this.reasoningEffort;
                }

                var response = await this.transport.SendAsync(
                    "turn/start",
                    new JObject
                    {
                        ["threadId"] = this.threadId,
                        ["input"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "text",
                                ["text"] = prompt,
                            },
                        },
                        ["model"] = string.IsNullOrWhiteSpace(currentModel) ? null : currentModel,
                        ["effort"] = string.IsNullOrWhiteSpace(currentReasoningEffort) ? null : currentReasoningEffort,
                    },
                    TimeSpan.FromSeconds(30),
                    cancellationToken).ConfigureAwait(false);
                var returnedTurnId = (response as JObject)?["turn"]?.Value<string>("id");
                if (string.IsNullOrWhiteSpace(returnedTurnId))
                {
                    throw new InvalidOperationException("Codex did not return a turn ID.");
                }

                lock (this.sync)
                {
                    this.activeTurnId = returnedTurnId;
                    started.TrySetResult(returnedTurnId);
                    foreach (var notification in this.pendingTurnNotifications)
                    {
                        this.ApplyTurnNotification(notification);
                    }

                    this.pendingTurnNotifications.Clear();
                }

                if (toolCancellation.IsCancellationRequested)
                {
                    this.Cancel();
                }

                var completed = await completion.Task.WaitAsync(TimeSpan.FromMinutes(10), toolCancellation.Token).ConfigureAwait(false);
                var status = completed.Value<string>("status");
                if (!string.Equals(status, "completed", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(ToTurnFailureMessage(completed, status));
                }

                var finalText = ExtractAgentText(completed);
                lock (this.sync)
                {
                    if (string.IsNullOrWhiteSpace(finalText))
                    {
                        finalText = this.streamedText.ToString();
                    }

                    var assistant = new AIInteractionText
                    {
                        Agent = AIAgent.Assistant,
                        Content = finalText,
                        TurnId = this.activeTurnId ?? Guid.NewGuid().ToString("N"),
                    };
                    this.history.Add(assistant);
                    this.UpdateLastReturn();
                    this.observer.OnInteractionCompleted(assistant);
                    this.observer.OnFinal(this.lastReturn);
                    return this.lastReturn;
                }
            }
            catch (Exception ex)
            {
                this.observer.OnError(ex);
                throw;
            }
            finally
            {
                lock (this.sync)
                {
                    this.turnCompleted = null;
                    this.activeTurnId = null;
                    this.activeToolCancellation = null;
                    this.turnStarted = null;
                    this.pendingTurnNotifications.Clear();
                }

                // A completed/cancelled turn must not leave approval waits or tool calls live.
                CancelTools(toolCancellation);
            }
        }

        private async Task EnsureThreadAsync(CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(this.threadId))
            {
                return;
            }

            string currentModel;
            lock (this.sync)
            {
                currentModel = this.model;
            }

            var parameters = new JObject
            {
                ["model"] = string.IsNullOrWhiteSpace(currentModel) ? null : currentModel,
                ["baseInstructions"] = this.systemPrompt,
                ["ephemeral"] = true,
                ["approvalPolicy"] = "never",
                ["sandbox"] = "read-only",
            };
            var dynamicToolsAdded = false;
            if (this.dynamicToolBridge != null && this.transport.SupportsDynamicTools)
            {
                var dynamicTools = this.dynamicToolBridge.BuildDynamicTools();
                if (dynamicTools.Count > 0)
                {
                    parameters["dynamicTools"] = dynamicTools;
                    dynamicToolsAdded = true;
                }
            }
            else if (this.dynamicToolBridge != null)
            {
                this.ReportDynamicToolsFallback();
            }

            JToken response;
            try
            {
                response = await this.transport.SendAsync(
                    "thread/start",
                    parameters,
                    TimeSpan.FromSeconds(30),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (CodexJsonRpcException ex) when (dynamicToolsAdded && (ex.Code == -32601 || ex.Code == -32602))
            {
                parameters.Remove("dynamicTools");
                this.ReportDynamicToolsFallback();
                response = await this.transport.SendAsync(
                    "thread/start",
                    parameters,
                    TimeSpan.FromSeconds(30),
                    cancellationToken).ConfigureAwait(false);
            }

            var returnedThreadId = (response as JObject)?["thread"]?.Value<string>("id");
            if (string.IsNullOrWhiteSpace(returnedThreadId))
            {
                throw new InvalidOperationException("Codex가 대화 thread ID를 반환하지 않았습니다.");
            }

            lock (this.sync)
            {
                this.ThrowIfDisposed();
                this.requestRouter?.Register(returnedThreadId, this.HandleServerRequestAsync);
                this.threadId = returnedThreadId;
            }
        }

        private void OnNotificationReceived(object? sender, CodexNotificationEventArgs notification)
        {
            lock (this.sync)
            {
                if (Volatile.Read(ref this.disposed) != 0 || this.turnCompleted == null ||
                    (notification.Method != "item/agentMessage/delta" && notification.Method != "turn/completed" && notification.Method != "thread/tokenUsage/updated") ||
                    notification.Parameters["threadId"]?.Type != JTokenType.String ||
                    !string.Equals(notification.Parameters.Value<string>("threadId"), this.threadId, StringComparison.Ordinal))
                {
                    return;
                }

                if (this.activeTurnId == null)
                {
                    this.pendingTurnNotifications.Add(notification);
                    return;
                }

                this.ApplyTurnNotification(notification);
            }
        }

        private void ApplyTurnNotification(CodexNotificationEventArgs notification)
        {
            var notificationTurnId = notification.Method == "turn/completed"
                ? notification.Parameters["turn"]?["id"]
                : notification.Parameters["turnId"];
            if (notificationTurnId?.Type != JTokenType.String ||
                !string.Equals(notificationTurnId.Value<string>(), this.activeTurnId, StringComparison.Ordinal))
            {
                return;
            }

            if (notification.Method == "thread/tokenUsage/updated")
            {
                var total = notification.Parameters["tokenUsage"]?["total"]?.Value<long?>("totalTokens");
                var last = notification.Parameters["tokenUsage"]?["last"]?.Value<long?>("totalTokens") ?? 0;
                if (total >= 0)
                {
                    this.tokenBaselineForTurn ??= Math.Max(0, total.Value - Math.Max(0, last));
                    this.reportedTotalTokens = total;
                    if (this.maxTokensForTurn > 0 && total.Value - this.tokenBaselineForTurn.Value >= this.maxTokensForTurn)
                        this.Cancel();
                }
            }
            else if (notification.Method == "item/agentMessage/delta")
            {
                var delta = notification.Parameters.Value<string>("delta");
                if (string.IsNullOrEmpty(delta))
                {
                    return;
                }

                string aggregate;
                string turnId;
                lock (this.sync)
                {
                    this.streamedText.Append(delta);
                    aggregate = this.streamedText.ToString();
                    turnId = notification.Parameters.Value<string>("turnId") ?? this.activeTurnId ?? "codex-stream";
                }

                this.observer.OnDelta(new AIInteractionText
                {
                    Agent = AIAgent.Assistant,
                    Content = aggregate,
                    TurnId = turnId,
                });
            }
            else if (notification.Method == "turn/completed" && notification.Parameters["turn"] is JObject turn)
            {
                this.turnCompleted?.TrySetResult(turn);
            }
        }

        private async Task HandleServerRequestAsync(
            CodexServerRequest request,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(request.Method, "item/tool/call", StringComparison.Ordinal) || this.dynamicToolBridge == null)
            {
                await this.transport.SendErrorAsync(
                    request.RequestId,
                    -32601,
                    "Client method not supported.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!CodexDynamicToolCall.TryParse(request.Parameters, out var call) || call == null)
            {
                await this.SendToolResultAsync(
                    request.RequestId,
                    CodexDynamicToolResult.Error("Canvas 도구 요청 형식이 올바르지 않습니다."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            string? currentThread;
            string? currentTurn;
            CancellationToken toolCancellation;
            Task<string>? started;
            Task<JObject>? completion;
            bool processTools;
            lock (this.sync)
            {
                processTools = this.processToolsForTurn;
                currentThread = this.threadId;
                currentTurn = this.activeTurnId;
                toolCancellation = this.activeToolCancellation?.Token ?? CancellationToken.None;
                started = this.turnStarted?.Task;
                completion = this.turnCompleted?.Task;
            }

            if (!processTools)
            {
                await this.SendToolResultAsync(request.RequestId, CodexDynamicToolResult.Error("Tool execution is disabled for this turn."), CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (currentTurn == null && started != null)
            {
                using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, toolCancellation);
                try
                {
                    currentTurn = await started.WaitAsync(startCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await this.SendToolResultAsync(request.RequestId, CodexDynamicToolResult.Error("Canvas tool execution was cancelled."), CancellationToken.None).ConfigureAwait(false);
                    return;
                }
            }

            if (Volatile.Read(ref this.disposed) != 0 || toolCancellation.IsCancellationRequested || completion == null || completion.IsCompleted)
            {
                await this.SendToolResultAsync(request.RequestId, CodexDynamicToolResult.Error("Canvas tool execution was cancelled."), CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(call.ThreadId, currentThread, StringComparison.Ordinal))
            {
                await this.SendToolResultAsync(
                    request.RequestId,
                    CodexDynamicToolResult.Error("현재 Canvas Assistant 대화에 속하지 않는 도구 요청입니다."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(currentTurn) || !string.Equals(call.TurnId, currentTurn, StringComparison.Ordinal))
            {
                await this.SendToolResultAsync(
                    request.RequestId,
                    CodexDynamicToolResult.Error("현재 활성 turn에 속하지 않는 도구 요청입니다."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(call.Namespace, CodexDynamicToolBridge.NamespaceName, StringComparison.Ordinal))
            {
                await this.SendToolResultAsync(
                    request.RequestId,
                    CodexDynamicToolResult.Error("지원하지 않는 Canvas 도구 namespace입니다."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!this.handledCallIds.TryAdd(call.CallId, 0))
            {
                await this.SendToolResultAsync(
                    request.RequestId,
                    CodexDynamicToolResult.Error("이미 처리한 Canvas 도구 요청입니다."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var toolInteraction = new AIInteractionToolCall
            {
                Id = call.CallId,
                Name = call.Tool,
                TurnId = call.TurnId,
                Arguments = (JObject)call.Arguments.DeepClone(),
            };
            this.observer.OnToolCall(toolInteraction);

            CodexDynamicToolResult result;
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                toolCancellation);
            try
            {
                linkedCancellation.Token.ThrowIfCancellationRequested();
                result = await this.dynamicToolBridge.ExecuteAsync(call, linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = CodexDynamicToolResult.Error("Canvas 도구 실행이 취소되었습니다.");
            }
            catch (Exception)
            {
                result = CodexDynamicToolResult.Error("Canvas 도구 실행 중 오류가 발생했습니다.");
            }

            this.observer.OnToolResult(new AIInteractionToolResult
            {
                Images = result.Images.ToList(),
                Id = call.CallId,
                Name = call.Tool,
                TurnId = call.TurnId,
                Result = result.Success
                    ? (JObject)result.Payload.DeepClone()
                    : new JObject { ["error"] = result.ErrorMessage },
            });
            await this.SendToolResultAsync(request.RequestId, result, CancellationToken.None).ConfigureAwait(false);
        }

        private Task SendToolResultAsync(
            JToken requestId,
            CodexDynamicToolResult result,
            CancellationToken cancellationToken)
        {
            return this.transport.SendResponseAsync(requestId, result.ToResponse(), cancellationToken);
        }

        private void ReportDynamicToolsFallback()
        {
            lock (this.sync)
            {
                if (this.dynamicToolsFallbackReported)
                {
                    return;
                }

                this.dynamicToolsFallbackReported = true;
                var status = new AIInteractionText
                {
                    Agent = AIAgent.System,
                    Content = "현재 Codex CLI가 Canvas Dynamic Tools를 지원하지 않아 읽기 전용 대화 모드로 실행합니다.",
                    TurnId = "codex-dynamic-tools-fallback",
                };
                this.history.Add(status);
                this.UpdateLastReturn();
                this.observer.OnInteractionCompleted(status);
            }
        }

        private static string AddCurrentContext(string userMessage)
        {
            var context = AIContextManager.GetCurrentContext("time, environment, current-file");
            if (context.Count == 0)
            {
                return userMessage;
            }

            var builder = new StringBuilder(userMessage);
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("[현재 Grasshopper 컨텍스트]");
            foreach (var item in context)
            {
                builder.Append(item.Key).Append(": ").AppendLine(item.Value);
            }

            return builder.ToString();
        }

        private static string ExtractAgentText(JObject turn)
        {
            return string.Join(
                "\n\n",
                (turn["items"] as JArray ?? new JArray())
                    .OfType<JObject>()
                    .Where(item => item.Value<string>("type") == "agentMessage")
                    .Select(item => item.Value<string>("text"))
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
        }

        private static string ToTurnFailureMessage(JObject turn, string? status)
        {
            var errorMessage = turn["error"]?.Value<string>("message");
            return !string.IsNullOrWhiteSpace(errorMessage)
                ? errorMessage
                : status == "interrupted" ? "Codex 응답이 취소되었습니다." : "Codex 응답 생성에 실패했습니다.";
        }

        private void UpdateLastReturn()
        {
            this.lastReturn = CreateReturn(this.history);
        }

        private static AIReturn CreateReturn(IEnumerable<IAIInteraction> interactions)
        {
            var result = new AIReturn
            {
                Status = AICallStatus.Finished,
                SkipRequestValidation = true,
                SkipMetricsValidation = true,
            };
            var builder = AIBodyBuilder.Create().AsHistory();
            foreach (var interaction in interactions)
            {
                builder.Add(interaction, markAsNew: false);
            }

            result.SetBody(builder.Build());
            return result;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref this.disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(CodexConversationSession));
            }
        }

        private static void CancelTools(CancellationTokenSource? cancellation)
        {
            try
            {
                cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The turn may finish after Cancel captured its source.
            }
        }
    }
}
