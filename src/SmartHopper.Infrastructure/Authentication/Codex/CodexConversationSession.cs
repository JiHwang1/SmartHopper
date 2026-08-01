/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public License.
 */

using System;
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
    public sealed class CodexConversationSession : IConversationSession, ICodexConfigurableConversationSession, IDisposable
    {
        private readonly ICodexConversationTransport transport;
        private readonly IConversationObserver observer;
        private readonly string systemPrompt;
        private readonly bool includeCurrentContext;
        private string model;
        private string reasoningEffort = string.Empty;
        private readonly object sync = new ();
        private readonly List<IAIInteraction> history = new ();
        private TaskCompletionSource<JObject>? turnCompleted;
        private StringBuilder streamedText = new ();
        private string? threadId;
        private string? activeTurnId;
        private string? pendingUserMessage;
        private AIReturn lastReturn = CreateReturn(Array.Empty<IAIInteraction>());
        private int disposed;

        public CodexConversationSession(
            ICodexConversationTransport transport,
            string model,
            string systemPrompt,
            IConversationObserver observer,
            bool includeCurrentContext = true)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.model = model ?? string.Empty;
            this.systemPrompt = systemPrompt ?? string.Empty;
            this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
            this.includeCurrentContext = includeCurrentContext;
            this.Request = new AIRequestCall
            {
                Provider = "Codex Account",
                Model = this.model,
                Endpoint = "canvas-chat",
                Capability = AIModels.AICapability.None,
            };
            this.transport.NotificationReceived += this.OnNotificationReceived;
        }

        public AIRequestCall Request { get; }

        public AIReturn LastReturn => this.lastReturn;

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
            return await this.RunTurnAsync(cancellationToken).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<AIReturn> Stream(
            SessionOptions options,
            StreamingOptions streamingOptions,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return await this.RunTurnAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Cancel()
        {
            string? currentThread;
            string? currentTurn;
            lock (this.sync)
            {
                currentThread = this.threadId;
                currentTurn = this.activeTurnId;
            }

            if (!string.IsNullOrWhiteSpace(currentThread) && !string.IsNullOrWhiteSpace(currentTurn))
            {
                _ = this.transport.SendAsync(
                    "turn/interrupt",
                    new JObject
                    {
                        ["threadId"] = currentThread,
                        ["turnId"] = currentTurn,
                    },
                    cancellationToken: CancellationToken.None);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            {
                return;
            }

            this.transport.NotificationReceived -= this.OnNotificationReceived;
            this.turnCompleted?.TrySetCanceled();
        }

        private async Task<AIReturn> RunTurnAsync(CancellationToken cancellationToken)
        {
            this.ThrowIfDisposed();
            string? userMessage;
            lock (this.sync)
            {
                userMessage = this.pendingUserMessage;
                this.pendingUserMessage = null;
            }

            if (string.IsNullOrWhiteSpace(userMessage))
            {
                return this.GetHistoryReturn();
            }

            try
            {
                await this.EnsureThreadAsync(cancellationToken).ConfigureAwait(false);
                var prompt = this.includeCurrentContext ? AddCurrentContext(userMessage) : userMessage;
                string currentModel;
                string currentReasoningEffort;
                var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (this.sync)
                {
                    currentModel = this.model;
                    currentReasoningEffort = this.reasoningEffort;
                    this.turnCompleted = completion;
                    this.streamedText = new StringBuilder();
                    this.activeTurnId = null;
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
                lock (this.sync)
                {
                    this.activeTurnId = (response as JObject)?["turn"]?.Value<string>("id");
                }

                var completed = await completion.Task.WaitAsync(TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
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
                }
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

            var response = await this.transport.SendAsync(
                "thread/start",
                new JObject
                {
                    ["model"] = string.IsNullOrWhiteSpace(currentModel) ? null : currentModel,
                    ["baseInstructions"] = this.systemPrompt,
                    ["ephemeral"] = true,
                    ["approvalPolicy"] = "never",
                    ["sandbox"] = "read-only",
                },
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            this.threadId = (response as JObject)?["thread"]?.Value<string>("id");
            if (string.IsNullOrWhiteSpace(this.threadId))
            {
                throw new InvalidOperationException("Codex가 대화 thread ID를 반환하지 않았습니다.");
            }
        }

        private void OnNotificationReceived(object? sender, CodexNotificationEventArgs notification)
        {
            var notificationThreadId = notification.Parameters.Value<string>("threadId");
            if (string.IsNullOrWhiteSpace(this.threadId) ||
                !string.Equals(notificationThreadId, this.threadId, StringComparison.Ordinal))
            {
                return;
            }

            if (notification.Method == "item/agentMessage/delta")
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
    }
}
