/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public License.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
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
using SmartHopper.Infrastructure.AIModels;
using SmartHopper.Infrastructure.AIProviders;
using SmartHopper.Infrastructure.Settings;
using SmartHopper.Infrastructure.Streaming;

namespace SmartHopper.Infrastructure.Authentication.Codex
{
    /// <summary>
    /// 인증된 Codex app-server를 통해 텍스트 요청을 실행하는 내장 Provider입니다.
    /// OAuth 인증 정보는 Codex가 계속 관리하며 SmartHopper는 이를 읽지 않습니다.
    /// </summary>
    public sealed class CodexAccountProvider : IAIProvider
    {
        public const string ProviderName = "Codex Account";

        private static readonly Lazy<CodexAccountProvider> InstanceFactory = new (
            () => new CodexAccountProvider(),
            LazyThreadSafetyMode.ExecutionAndPublication);

        private CodexAccountProvider()
        {
            this.Models = new CodexAccountProviderModels(this);
        }

        public static CodexAccountProvider Instance => InstanceFactory.Value;

        public string Name => ProviderName;

        public Image Icon => SystemIcons.Application.ToBitmap();

        public bool IsEnabled => true;

        public IAIProviderModels Models { get; }

        public async Task InitializeProviderAsync()
        {
            var models = await this.Models.RetrieveModels().ConfigureAwait(false);
            foreach (var model in models)
            {
                ModelManager.Instance.SetCapabilities(model);
            }
        }

        public string Encode(AIRequestCall request)
        {
            return BuildPrompt(request?.Body, request?.Capability ?? AICapability.None);
        }

        public string Encode(IAIInteraction interaction)
        {
            return interaction?.ToString() ?? string.Empty;
        }

        public string Encode(List<IAIInteraction> interactions)
        {
            return string.Join("\n\n", interactions?.Select(this.Encode) ?? Enumerable.Empty<string>());
        }

        public List<IAIInteraction> Decode(JObject response)
        {
            var text = response?.Value<string>("text") ?? string.Empty;
            return string.IsNullOrWhiteSpace(text)
                ? new List<IAIInteraction>()
                : new List<IAIInteraction>
                {
                    new AIInteractionText
                    {
                        Agent = AIAgent.Assistant,
                        Content = text,
                        TurnId = Guid.NewGuid().ToString("N"),
                    },
                };
        }

        public AIRequestCall PreCall(AIRequestCall request)
        {
            if (request != null)
            {
                request.Provider = ProviderName;
                if (string.IsNullOrWhiteSpace(request.Endpoint))
                {
                    request.Endpoint = "codex-app-server";
                }
            }

            return request;
        }

        public async Task<IAIReturn> Call(AIRequestCall request, CancellationToken cancellationToken = default)
        {
            request = this.PreCall(request);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var model = this.SelectModel(request?.Capability ?? AICapability.Text2Text, request?.Model);
                if (string.IsNullOrWhiteSpace(model))
                {
                    return CreateError("Canvas Assistant에서 Codex 모델을 먼저 선택하세요.", request);
                }

                EnsureModelRegistered(model);
                var reasoningEffort = ResolveReasoningEffort(request);
                var systemPrompt = BuildSystemPrompt(request?.Body);
                var prompt = BuildPrompt(request?.Body, request?.Capability ?? AICapability.None);
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    return CreateError("Codex에 전달할 텍스트 입력이 없습니다.", request);
                }

                using var session = new CodexConversationSession(
                    CodexAccountRuntime.ConversationTransport,
                    model,
                    systemPrompt,
                    NullConversationObserver.Instance,
                    includeCurrentContext: false);
                session.Configure(model, reasoningEffort);
                session.AddInteraction(prompt);
                var sessionResult = await session.RunToStableResult(
                    new SessionOptions(),
                    cancellationToken).ConfigureAwait(false);
                var responseText = sessionResult.Body.GetLastAssistantText();
                if (string.IsNullOrWhiteSpace(responseText))
                {
                    return CreateError("Codex가 텍스트 응답을 반환하지 않았습니다.", request);
                }

                stopwatch.Stop();
                request.Model = model;
                var interaction = new AIInteractionText
                {
                    Agent = AIAgent.Assistant,
                    Content = responseText,
                    TurnId = Guid.NewGuid().ToString("N"),
                    Metrics = new AIMetrics
                    {
                        Provider = ProviderName,
                        Model = model,
                        FinishReason = "stop",
                        CompletionTime = stopwatch.Elapsed.TotalSeconds,
                    },
                };
                var result = new AIReturn();
                result.CreateSuccess(new List<IAIInteraction> { interaction }, request);
                return result;
            }
            catch (OperationCanceledException)
            {
                return CreateError("Codex 요청이 취소되었습니다.", request);
            }
            catch (Exception ex)
            {
                return CreateError(ex.Message, request);
            }
        }

        public IAIReturn PostCall(IAIReturn response) => response;

        public string GetDefaultModel(AICapability requiredCapability = AICapability.Text2Text, bool useSettings = true)
        {
            var assistant = SmartHopperSettings.Instance.SmartHopperAssistant;
            return !string.IsNullOrWhiteSpace(assistant?.CodexModel)
                ? assistant.CodexModel
                : assistant?.AssistantModel ?? string.Empty;
        }

        public string SelectModel(AICapability requiredCapability, string requestedModel)
        {
            if (!Supports(requiredCapability))
            {
                return string.Empty;
            }

            return !string.IsNullOrWhiteSpace(requestedModel)
                ? requestedModel.Trim()
                : this.GetDefaultModel(requiredCapability);
        }

        public void RefreshCachedSettings(Dictionary<string, object> settings)
        {
        }

        public IEnumerable<SettingDescriptor> GetSettingDescriptors() => Array.Empty<SettingDescriptor>();

        public IStreamingAdapter GetStreamingAdapter() => null;

        public IEnumerable<AIExtraDescriptor> GetExtraDescriptors() => Array.Empty<AIExtraDescriptor>();

        internal static bool Supports(AICapability capability)
        {
            const AICapability supported = AICapability.TextInput |
                AICapability.TextOutput |
                AICapability.JsonOutput |
                AICapability.Reasoning;
            return capability == AICapability.None || (capability & supported) == capability;
        }

        internal static void EnsureModelRegistered(string model)
        {
            if (string.IsNullOrWhiteSpace(model) || ModelManager.Instance.GetCapabilities(ProviderName, model) != null)
            {
                return;
            }

            ModelManager.Instance.SetCapabilities(CodexAccountProviderModels.CreateCapabilities(model));
        }

        private static string ResolveReasoningEffort(AIRequestCall request)
        {
            if (request?.Parameters?.Extras != null &&
                request.Parameters.Extras.TryGetValue("reasoning_effort", out var effortToken) &&
                effortToken.Type == JTokenType.String)
            {
                return effortToken.Value<string>()?.Trim() ?? string.Empty;
            }

            return SmartHopperSettings.Instance.SmartHopperAssistant?.CodexReasoningEffort ?? string.Empty;
        }

        private static string BuildSystemPrompt(AIBody body)
        {
            return string.Join(
                "\n\n",
                body?.Interactions?.OfType<AIInteractionText>()
                    .Where(item => item.Agent == AIAgent.System || item.Agent == AIAgent.Summary)
                    .Select(item => item.Content)
                    .Where(content => !string.IsNullOrWhiteSpace(content))
                ?? Enumerable.Empty<string>());
        }

        private static string BuildPrompt(AIBody body, AICapability capability)
        {
            var builder = new StringBuilder();
            foreach (var interaction in body?.Interactions ?? Array.Empty<IAIInteraction>())
            {
                if (interaction.Agent == AIAgent.System || interaction.Agent == AIAgent.Summary)
                {
                    continue;
                }

                var content = interaction is AIInteractionText text ? text.Content : interaction.ToString();
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.AppendLine().AppendLine();
                }

                builder.Append('[').Append(interaction.Agent).AppendLine("]");
                builder.Append(content);
            }

            if ((capability & AICapability.JsonOutput) == AICapability.JsonOutput)
            {
                builder.AppendLine().AppendLine();
                builder.Append("Return only valid JSON without markdown fences.");
            }

            return builder.ToString();
        }

        private static AIReturn CreateError(string message, AIRequestCall request)
        {
            var result = new AIReturn();
            result.CreateProviderError(message, request);
            result.Status = AICallStatus.Finished;
            return result;
        }

        private sealed class NullConversationObserver : IConversationObserver
        {
            public static readonly NullConversationObserver Instance = new ();

            public void OnStart(AIRequestCall request)
            {
            }

            public void OnDelta(IAIInteraction interaction)
            {
            }

            public void OnInteractionCompleted(IAIInteraction interaction)
            {
            }

            public void OnToolCall(AIInteractionToolCall toolCall)
            {
            }

            public void OnToolResult(AIInteractionToolResult toolResult)
            {
            }

            public void OnFinal(AIReturn finalResult)
            {
            }

            public void OnError(Exception error)
            {
            }
        }
    }
}
