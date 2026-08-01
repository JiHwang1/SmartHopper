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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SmartHopper.Infrastructure.AICall.Core.Base;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.Core.Returns;
using SmartHopper.Infrastructure.AICall.Tools;
using SmartHopper.Infrastructure.Diagnostics;

namespace SmartHopper.Infrastructure.AITools.External
{
    /// <summary>
    /// MCP와 Codex 같은 외부 호출 표면에 적용할 공통 도구 노출 정책입니다.
    /// </summary>
    public sealed class AIToolExternalCatalogOptions
    {
        public IReadOnlyCollection<string>? EnabledTools { get; init; }

        public bool ExposeMutatingTools { get; init; }

        public bool AllowListOverridesMutatingFilter { get; init; }

        public bool ExcludeDestructiveTools { get; init; }

        public Func<AITool, bool>? AdditionalFilter { get; init; }
    }

    /// <summary>
    /// 특정 프로토콜 형식에 종속되지 않는 SmartHopper 도구 설명입니다.
    /// </summary>
    public sealed class AIToolExternalDescriptor
    {
        internal AIToolExternalDescriptor(AITool tool, JObject inputSchema, JObject outputSchema)
        {
            this.Name = tool.Name;
            this.Description = tool.RichDescription;
            this.InputSchema = inputSchema;
            this.OutputSchema = outputSchema;
            this.MutatesCanvas = tool.MutatesCanvas;
            this.Tags = tool.Tags;
            this.Annotations = tool.Annotations;
        }

        public string Name { get; }

        public string Description { get; }

        public JObject InputSchema { get; }

        public JObject OutputSchema { get; }

        public bool MutatesCanvas { get; }

        public IReadOnlyList<string> Tags { get; }

        public AIToolAnnotations Annotations { get; }
    }

    /// <summary>
    /// AIToolManager catalog을 외부 호출용 descriptor와 정책 검사로 변환합니다.
    /// </summary>
    public sealed class AIToolExternalCatalog
    {
        private readonly Func<IReadOnlyDictionary<string, AITool>> toolSource;

        public AIToolExternalCatalog()
            : this(() => AIToolManager.GetTools())
        {
        }

        public AIToolExternalCatalog(Func<IReadOnlyDictionary<string, AITool>> toolSource)
        {
            this.toolSource = toolSource ?? throw new ArgumentNullException(nameof(toolSource));
        }

        public IReadOnlyList<AIToolExternalDescriptor> BuildDescriptors(AIToolExternalCatalogOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            return this.toolSource().Values
                .Where(tool => this.IsExposed(tool.Name, options))
                .Select(tool => new AIToolExternalDescriptor(
                    tool,
                    ParseSchema(tool.ParametersSchema),
                    ParseSchema(tool.OutputSchema)))
                .OrderBy(tool => tool.Name, StringComparer.Ordinal)
                .ToList();
        }

        public bool IsExposed(string toolName, AIToolExternalCatalogOptions options)
        {
            if (!this.TryGetTool(toolName, out var tool) || !tool.Enabled)
            {
                return false;
            }

            var hasAllowList = options.EnabledTools != null && options.EnabledTools.Count > 0;
            var isAllowed = !hasAllowList || options.EnabledTools!.Any(name =>
                string.Equals(name, toolName, StringComparison.OrdinalIgnoreCase));
            if (!isAllowed)
            {
                return false;
            }

            if (tool.MutatesCanvas &&
                !options.ExposeMutatingTools &&
                !(hasAllowList && options.AllowListOverridesMutatingFilter))
            {
                return false;
            }

            if (options.ExcludeDestructiveTools && tool.Annotations.DestructiveHint == true)
            {
                return false;
            }

            return options.AdditionalFilter?.Invoke(tool) ?? true;
        }

        public bool TryGetTool(string toolName, out AITool tool)
        {
            tool = null!;
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return false;
            }

            return this.toolSource().TryGetValue(toolName, out tool!);
        }

        private static JObject ParseSchema(string schema)
        {
            if (!string.IsNullOrWhiteSpace(schema))
            {
                try
                {
                    if (JToken.Parse(schema) is JObject parsed)
                    {
                        return parsed;
                    }
                }
                catch (JsonReaderException)
                {
                    // 지원하지 않는 schema는 object로 제한해 외부 대화 자체는 유지합니다.
                }
            }

            return new JObject { ["type"] = "object" };
        }
    }

    /// <summary>
    /// 외부 도구 실행 결과를 프로토콜과 독립적인 형태로 표현합니다.
    /// </summary>
    public sealed class AIToolExternalResult
    {
        private AIToolExternalResult(bool isError, JObject payload, string? errorMessage)
        {
            this.IsError = isError;
            this.Payload = payload;
            this.ErrorMessage = errorMessage;
        }

        public bool IsError { get; }

        public JObject Payload { get; }

        public string? ErrorMessage { get; }

        public static AIToolExternalResult Ok(JObject payload)
        {
            return new AIToolExternalResult(false, payload, null);
        }

        public static AIToolExternalResult Error(string message)
        {
            return new AIToolExternalResult(true, new JObject(), message);
        }
    }

    /// <summary>
    /// AIReturn을 외부 도구 결과로 일관되게 변환합니다.
    /// </summary>
    public static class AIToolExternalResultMapper
    {
        public static AIToolExternalResult Map(string toolName, AIReturn? result)
        {
            if (result == null)
            {
                return AIToolExternalResult.Error($"Tool '{toolName}' returned no result");
            }

            var lastToolResult = result.Body?.Interactions?
                .OfType<AIInteractionToolResult>()
                .LastOrDefault();
            if (lastToolResult?.Result != null)
            {
                return AIToolExternalResult.Ok((JObject)lastToolResult.Result.DeepClone());
            }

            var firstError = result.Messages?
                .FirstOrDefault(message => message?.Severity == SHRuntimeMessageSeverity.Error);
            if (firstError != null)
            {
                return AIToolExternalResult.Error(firstError.Message ?? $"Tool '{toolName}' failed");
            }

            return AIToolExternalResult.Ok(new JObject());
        }
    }

    /// <summary>
    /// 외부 JSON 호출을 AIToolCall로 만들고 기존 실행 파이프라인으로 전달합니다.
    /// </summary>
    public sealed class AIToolExternalExecutor
    {
        private readonly AIToolExternalCatalog catalog;
        private readonly Func<AIToolCall, Task<AIReturn>> executor;

        public AIToolExternalExecutor(AIToolExternalCatalog catalog)
            : this(catalog, call => call.Exec(call.CancellationToken))
        {
        }

        public AIToolExternalExecutor(
            AIToolExternalCatalog catalog,
            Func<AIToolCall, Task<AIReturn>> executor)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        public async Task<AIToolExternalResult> ExecuteAsync(
            string toolName,
            JObject? arguments,
            AIToolExternalCatalogOptions options,
            string? callId = null,
            string? turnId = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return AIToolExternalResult.Error("Tool name is required");
            }

            if (!this.catalog.IsExposed(toolName, options))
            {
                return AIToolExternalResult.Error($"Tool '{toolName}' is not exposed");
            }

            var interaction = new AIInteractionToolCall
            {
                Id = string.IsNullOrWhiteSpace(callId) ? Guid.NewGuid().ToString("N") : callId,
                TurnId = turnId,
                Name = toolName,
                Arguments = arguments != null ? (JObject)arguments.DeepClone() : new JObject(),
            };
            var toolCall = new AIToolCall
            {
                CancellationToken = cancellationToken,
                SkipMetricsValidation = true,
            };
            toolCall.FromToolCallInteraction(interaction);

            try
            {
                var result = await this.executor(toolCall).ConfigureAwait(false);
                return AIToolExternalResultMapper.Map(toolName, result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return AIToolExternalResult.Error($"Tool '{toolName}' was cancelled");
            }
            catch (Exception)
            {
                return AIToolExternalResult.Error($"Tool '{toolName}' failed to execute");
            }
        }
    }
}
