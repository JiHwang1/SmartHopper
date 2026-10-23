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
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SmartHopper.Infrastructure.AICall.Core.Base;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.Core.Requests;
using SmartHopper.Infrastructure.AICall.Core.Returns;
using SmartHopper.Infrastructure.AICall.Metrics;
using SmartHopper.Infrastructure.AICall.Policies;
using SmartHopper.Infrastructure.AICall.Policies.Response;
using SmartHopper.Infrastructure.Diagnostics;
using Xunit;

namespace SmartHopper.Infrastructure.Tests.AICall
{
    public class UsageAndFailureTests
    {
#if NET7_WINDOWS
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void OpenAIDecode_UsesNetGenerationTokens(bool responses)
        {
            var payload = new JObject { ["usage"] = Usage(responses, 100, 60, 25, 20) };
            if (responses)
            {
                payload["output"] = new JArray(new JObject
                {
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["content"] = new JArray(new JObject { ["type"] = "output_text", ["text"] = "done" }),
                });
            }
            else
            {
                payload["choices"] = new JArray(new JObject
                {
                    ["finish_reason"] = "stop",
                    ["message"] = new JObject { ["role"] = "assistant", ["content"] = "done" },
                });
            }

            var decoded = SmartHopper.Providers.OpenAI.OpenAIProvider.Instance.Decode(payload);
            var metrics = Assert.Single(decoded.OfType<AIInteractionText>()).Metrics;
            Assert.Equal(25, metrics.OutputTokensReasoning);
            Assert.Equal(35, metrics.OutputTokensGeneration);
            Assert.Equal(160, metrics.TotalTokens);
        }
#endif

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void UsageSnapshot_SeparatesReasoningFromGeneration(bool responsesApi)
        {
            var metrics = new AIMetrics();
            OpenAIUsage.ApplyTo(metrics, Usage(responsesApi, 100, 60, 25, 20));

            Assert.Equal(80, metrics.InputTokensPrompt);
            Assert.Equal(20, metrics.InputTokensCached);
            Assert.Equal(25, metrics.OutputTokensReasoning);
            Assert.Equal(35, metrics.OutputTokensGeneration);
            Assert.Equal(160, metrics.TotalTokens);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void StreamingUsage_IsCumulative_NotAddedTwice(bool responsesApi)
        {
            var aggregate = new AIInteractionText { Agent = AIAgent.Assistant };
            aggregate.AppendDelta(contentDelta: "some text");
            OpenAIUsage.ApplyTo(aggregate.Metrics, Usage(responsesApi, 100, 30, 20, 10));
            aggregate.AppendDelta(contentDelta: " more text");
            OpenAIUsage.ApplyTo(aggregate.Metrics, Usage(responsesApi, 100, 60, 25, 20));
            OpenAIUsage.ApplyTo(aggregate.Metrics, Usage(responsesApi, 100, 60, 25, 20));

            Assert.Equal(100, aggregate.Metrics.InputTokens);
            Assert.Equal(60, aggregate.Metrics.OutputTokens);
            Assert.Equal(35, aggregate.Metrics.OutputTokensGeneration);
            Assert.Equal(25, aggregate.Metrics.OutputTokensReasoning);
        }

        [Fact]
        public void MissingUsage_DoesNotInventTokens_AndPartialUsageKeepsOtherCounts()
        {
            var metrics = new AIMetrics();
            OpenAIUsage.ApplyTo(metrics, null);
            OpenAIUsage.ApplyTo(metrics, new JObject());
            Assert.False(metrics.HasProviderCallEvidence);
            OpenAIUsage.ApplyTo(metrics, Usage(false, 100, 30, 20, 10));
            OpenAIUsage.ApplyTo(metrics, new JObject { ["prompt_tokens"] = 120 });
            Assert.Equal(30, metrics.OutputTokens);
            Assert.Equal(120, metrics.InputTokens);
        }

        [Fact]
        public void ReasoningAboveOutput_ClampsGenerationToZero()
        {
            var metrics = new AIMetrics();
            OpenAIUsage.ApplyTo(metrics, Usage(false, 100, 20, 30, 0));
            Assert.Equal(0, metrics.OutputTokensGeneration);
            Assert.Equal(20, metrics.OutputTokensReasoning);
            Assert.Equal(20, metrics.OutputTokens);
        }

        [Fact]
        public void MalformedCachedTokens_AreBoundedByReportedInput()
        {
            var metrics = new AIMetrics();
            OpenAIUsage.ApplyTo(metrics, Usage(false, 10, 20, -4, 40));
            Assert.Equal(10, metrics.InputTokensCached);
            Assert.Equal(0, metrics.InputTokensPrompt);
            Assert.Equal(10, metrics.InputTokens);
            Assert.Equal(0, metrics.OutputTokensReasoning);
            Assert.Equal(20, metrics.OutputTokensGeneration);
        }

        [Fact]
        public void RoutingMetadataAlone_IsNotProviderCallEvidence()
        {
            var metrics = new AIMetrics { Provider = "OpenAI", Model = "test" };
            Assert.False(metrics.HasProviderCallEvidence);
            metrics.IterationsCount = 1;
            Assert.False(metrics.HasProviderCallEvidence);
            metrics.EstimatedOutputTokens = 1;
            Assert.True(metrics.HasProviderCallEvidence);
        }

        [Fact]
        public void LocalWrapper_PreservesNestedProviderUsageAndIdentity()
        {
            var inner = new AIMetrics
            {
                Provider = "fallback-provider", Model = "fallback-model",
                InputTokensPrompt = 9, OutputTokensReasoning = 3, OutputTokensGeneration = 4,
                CompletionTime = 0.5, FinishReason = "stop",
            };
            var body = AIBodyBuilder.Create().Add(new AIInteractionToolResult
            {
                Id = "conversion", Name = "describe", Result = new JObject { ["result"] = "description" }, Metrics = inner,
            }).Build();
            var result = new AIReturn();
            result.CreateSuccess(body, new AIRequestCall { Provider = "outer-routing", Model = "outer-model" });

            Assert.True(result.Metrics.HasProviderCallEvidence);
            Assert.Equal("fallback-provider", result.Metrics.Provider);
            Assert.Equal("fallback-model", result.Metrics.Model);
            Assert.Equal(16, result.Metrics.TotalTokens);
            Assert.Equal(0.5, result.Metrics.CompletionTime);
        }

        [Theory]
        [InlineData("provider", SHRuntimeMessageOrigin.Provider, "Provider error: failed")]
        [InlineData("network", SHRuntimeMessageOrigin.Network, "Network error: failed")]
        [InlineData("tool", SHRuntimeMessageOrigin.Tool, "Tool error: failed")]
        [InlineData("return", SHRuntimeMessageOrigin.Return, "failed")]
        public async Task Failure_IsOneMatchingDiagnostic_AndDoesNotDefaultToStop(string kind, SHRuntimeMessageOrigin origin, string text)
        {
            var result = new AIReturn { SkipRequestValidation = true, SkipMetricsValidation = true };
            switch (kind)
            {
                case "provider": result.CreateProviderError("failed"); break;
                case "network": result.CreateNetworkError("failed"); break;
                case "tool": result.CreateToolError("failed"); break;
                default: result.CreateError("failed"); break;
            }

            await new FinishReasonNormalizeResponsePolicy().ApplyAsync(new PolicyContext { Response = result });

            var diagnostic = Assert.Single(result.Body.Interactions.OfType<AIInteractionRuntimeMessage>());
            Assert.Equal(origin, diagnostic.Origin);
            Assert.Equal(text, diagnostic.Content);
            Assert.Single(result.Messages.Where(m => m.Origin == origin && m.Message == text));
            Assert.True(string.IsNullOrWhiteSpace(result.Metrics.FinishReason));
            Assert.DoesNotContain(result.Messages, m => m.Message.Contains("defaulted to 'stop'", StringComparison.Ordinal));
            Assert.False(result.Metrics.HasProviderCallEvidence);
        }

        [Fact]
        public void IdenticalTextWithDifferentSeverityOrOrigin_RemainsDistinct()
        {
            var result = new AIReturn { SkipRequestValidation = true, SkipMetricsValidation = true };
            result.AddRuntimeMessage(SHRuntimeMessageSeverity.Warning, SHRuntimeMessageOrigin.Provider, "Integrity failed");
            result.SetBody(AIBodyBuilder.Create()
                .Add(new AIInteractionRuntimeMessage { Content = "Integrity failed", Severity = SHRuntimeMessageSeverity.Error, Origin = SHRuntimeMessageOrigin.Provider })
                .Add(new AIInteractionRuntimeMessage { Content = "Integrity failed", Severity = SHRuntimeMessageSeverity.Error, Origin = SHRuntimeMessageOrigin.Validation })
                .Build());

            Assert.Equal(3, result.Messages.Count);
            Assert.Equal(2, result.Messages.Count(m => m.Severity == SHRuntimeMessageSeverity.Error));
        }

        [Fact]
        public async Task SuccessfulResponse_StillNormalizesFinishReason()
        {
            var result = new AIReturn { SkipRequestValidation = true, SkipMetricsValidation = true };
            result.SetBody(AIBodyBuilder.Create().Add(new AIInteractionText
            {
                Agent = AIAgent.Assistant, Content = "done", Metrics = new AIMetrics { FinishReason = "end_turn" },
            }).Build());
            await new FinishReasonNormalizeResponsePolicy().ApplyAsync(new PolicyContext { Response = result });
            Assert.Equal("stop", result.Metrics.FinishReason);
        }

        private static JObject Usage(bool responses, int input, int output, int reasoning, int cached) => new ()
        {
            [responses ? "input_tokens" : "prompt_tokens"] = input,
            [responses ? "output_tokens" : "completion_tokens"] = output,
            [responses ? "input_tokens_details" : "prompt_tokens_details"] = new JObject { ["cached_tokens"] = cached },
            [responses ? "output_tokens_details" : "completion_tokens_details"] = new JObject { ["reasoning_tokens"] = reasoning },
        };
    }
}
