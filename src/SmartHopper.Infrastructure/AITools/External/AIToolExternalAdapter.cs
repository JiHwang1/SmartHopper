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

        public AIToolSurface Surface { get; init; } = AIToolSurface.Mcp;

        public SmartHopper.Infrastructure.Consent.MutationInvocationContext? InvocationContext { get; init; }
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
        private readonly object schemaCacheLock = new object();
        private readonly Dictionary<string, CachedSchemas> schemaCache = new Dictionary<string, CachedSchemas>(StringComparer.Ordinal);

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

            var tools = this.toolSource().Values.ToList();
            var enabledTools = options.EnabledTools == null
                ? null
                : new HashSet<string>(options.EnabledTools, StringComparer.OrdinalIgnoreCase);
            var hasAllowList = enabledTools != null && enabledTools.Count > 0;
            var exposedTools = tools
                .Where(tool => tool.Advertise)
                .Where(tool => this.IsExposed(tool, tool.Name, options, enabledTools, hasAllowList))
                .ToList();

            lock (this.schemaCacheLock)
            {
                var currentNames = new HashSet<string>(tools.Select(tool => tool.Name), StringComparer.Ordinal);
                foreach (var name in this.schemaCache.Keys.Where(name => !currentNames.Contains(name)).ToList())
                {
                    this.schemaCache.Remove(name);
                }

                return exposedTools
                    .Select(tool =>
                    {
                        var schemas = this.GetSchemas(tool);
                        return new AIToolExternalDescriptor(tool, (JObject)schemas.Input.DeepClone(), (JObject)schemas.Output.DeepClone());
                    })
                    .OrderBy(tool => tool.Name, StringComparer.Ordinal)
                    .ToList();
            }
        }

        public bool IsExposed(string toolName, AIToolExternalCatalogOptions options)
        {
            if (!this.TryGetTool(toolName, out var tool))
            {
                return false;
            }

            var enabledTools = options.EnabledTools == null
                ? null
                : new HashSet<string>(options.EnabledTools, StringComparer.OrdinalIgnoreCase);
            return this.IsExposed(tool, toolName, options, enabledTools, enabledTools != null && enabledTools.Count > 0);
        }

        private bool IsExposed(AITool tool, string toolName, AIToolExternalCatalogOptions options, HashSet<string>? enabledTools, bool hasAllowList)
        {
            if (!tool.Enabled || (tool.Surfaces & options.Surface) == 0)
            {
                return false;
            }

            if (hasAllowList && !enabledTools!.Contains(toolName))
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

        private CachedSchemas GetSchemas(AITool tool)
        {
            if (this.schemaCache.TryGetValue(tool.Name, out var cached) &&
                ReferenceEquals(cached.Tool, tool) &&
                string.Equals(cached.InputText, tool.ParametersSchema, StringComparison.Ordinal) &&
                string.Equals(cached.OutputText, tool.OutputSchema, StringComparison.Ordinal))
            {
                return cached;
            }

            cached = new CachedSchemas(tool, tool.ParametersSchema, tool.OutputSchema, ParseSchema(tool.ParametersSchema), ParseSchema(tool.OutputSchema));
            this.schemaCache[tool.Name] = cached;
            return cached;
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

        private sealed class CachedSchemas
        {
            public CachedSchemas(AITool tool, string inputText, string outputText, JObject input, JObject output)
            {
                this.Tool = tool;
                this.InputText = inputText;
                this.OutputText = outputText;
                this.Input = input;
                this.Output = output;
            }

            public AITool Tool { get; }

            public string InputText { get; }

            public string OutputText { get; }

            public JObject Input { get; }

            public JObject Output { get; }
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

        public IReadOnlyList<ToolResultImage> Images { get; init; } = Array.Empty<ToolResultImage>();

        public static AIToolExternalResult Ok(JObject payload, IReadOnlyList<ToolResultImage>? images = null)
        {
            return new AIToolExternalResult(false, payload, null) { Images = images ?? Array.Empty<ToolResultImage>() };
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
                return AIToolExternalResult.Ok((JObject)lastToolResult.Result.DeepClone(), lastToolResult.Images);
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
                Surface = options.Surface,
                InvocationContext = options.InvocationContext?.ForTool(interaction.Id, toolName),
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
