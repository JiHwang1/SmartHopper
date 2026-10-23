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
using Newtonsoft.Json.Linq;

namespace SmartHopper.Infrastructure.AICall.Metrics
{
    /// <summary>
    /// Applies cumulative OpenAI-compatible usage snapshots (including Responses API fields).
    /// Completion/output totals include reasoning; generation is the remaining output.
    /// </summary>
    public static class OpenAIUsage
    {
        public static void ApplyTo(AIMetrics metrics, JObject usage)
        {
            if (metrics == null || usage == null)
            {
                return;
            }

            var input = usage.Value<int?>("prompt_tokens") ?? usage.Value<int?>("input_tokens");
            if (input.HasValue)
            {
                var cached = usage["prompt_tokens_details"]?.Value<int?>("cached_tokens")
                    ?? usage["input_tokens_details"]?.Value<int?>("cached_tokens")
                    ?? usage.Value<int?>("prompt_cache_hit_tokens") ?? 0;
                var totalInput = Math.Max(0, input.Value);
                metrics.InputTokensCached = Math.Min(totalInput, Math.Max(0, cached));
                metrics.InputTokensPrompt = totalInput - metrics.InputTokensCached;
            }

            var output = usage.Value<int?>("completion_tokens") ?? usage.Value<int?>("output_tokens");
            if (output.HasValue)
            {
                var reasoning = usage["completion_tokens_details"]?.Value<int?>("reasoning_tokens")
                    ?? usage["output_tokens_details"]?.Value<int?>("reasoning_tokens") ?? 0;
                var totalOutput = Math.Max(0, output.Value);
                metrics.OutputTokensReasoning = Math.Min(totalOutput, Math.Max(0, reasoning));
                metrics.OutputTokensGeneration = totalOutput - metrics.OutputTokensReasoning;
            }
        }
    }
}
