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
using System.Reflection;
using Newtonsoft.Json.Linq;
using SmartHopper.Core.ComponentBase.Batch;
using SmartHopper.Infrastructure.AICall.Core.Base;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.Metrics;
using Xunit;

namespace SmartHopper.Core.Tests.UI
{
    public class ChatMetricsTests
    {
        [Fact]
        public void LocalToolWithProviderRouting_HidesMetricsIcon()
        {
            var html = Render(new AIInteractionToolResult
            {
                Id = "local", Name = "web2md", Result = new JObject { ["content"] = "local result" },
                Metrics = new AIMetrics { Provider = "routing-provider", Model = "routing-model" },
            });

            Assert.Contains("metrics-icon hidden", html);
            Assert.DoesNotContain("data-provider=\"routing-provider\"", html);
        }

        [Fact]
        public void NestedProviderUsageOnToolResult_ShowsMetricsIcon()
        {
            var html = Render(new AIInteractionToolResult
            {
                Id = "nested", Name = "describe", Result = new JObject { ["content"] = "AI result" },
                Metrics = new AIMetrics { Provider = "inner-provider", Model = "inner-model", InputTokensPrompt = 9, OutputTokensGeneration = 4 },
            });

            Assert.DoesNotContain("metrics-icon hidden", html);
            Assert.Contains("data-provider=\"inner-provider\"", html);
            Assert.Contains("data-in=\"9\"", html);
            Assert.Contains("data-out=\"4\"", html);
        }

        [Fact]
        public void ProviderCallWithNoReportedTokens_StillShowsMeasuredMetrics()
        {
            var html = Render(new AIInteractionText
            {
                Agent = AIAgent.Assistant, Content = "answer",
                Metrics = new AIMetrics { CompletionTime = 0.5 },
            });
            Assert.DoesNotContain("metrics-icon hidden", html);
        }

        private static string Render(IAIInteraction interaction)
        {
            var type = typeof(BatchSentinel).Assembly.GetType("SmartHopper.Core.UI.Chat.HtmlChatRenderer", true)!;
            var renderer = Activator.CreateInstance(type)!;
            return (string)type.GetMethod("RenderInteraction", BindingFlags.Public | BindingFlags.Instance)!.Invoke(renderer, new object[] { interaction })!;
        }
    }
}
