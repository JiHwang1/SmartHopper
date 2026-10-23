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
 * The reflection-based tool-discovery pattern is structurally adapted from Cordyceps
 * (https://github.com/brookstalley/cordyceps, McpServer.cs). SmartHopper does not
 * use [McpServerTool] reflection: tools are already discoverable through
 * AIToolManager, so the adapter projects that catalog into MCP descriptors instead.
 * Copyright (c) 2026 Brooks Talley. Licensed under the MIT License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SmartHopper.Infrastructure.AICall.Core.Returns;
using SmartHopper.Infrastructure.AICall.Tools;
using SmartHopper.Infrastructure.AITools;
using SmartHopper.Infrastructure.AITools.External;

namespace SmartHopper.Infrastructure.Mcp
{
    /// <summary>
    /// Bridges SmartHopper's <see cref="AIToolManager"/> catalog to the Model Context
    /// Protocol's <c>tools/list</c> and <c>tools/call</c> surface.
    /// </summary>
    /// <remarks>
    /// All schema-layer concerns (GhJSON marshalling, validation, fix-up, placement)
    /// continue to live inside the tools themselves and the upstream
    /// <c>architects-toolkit/ghjson-dotnet</c> library. This adapter only translates
    /// between the MCP envelope and <see cref="AIToolCall"/> / <see cref="AIReturn"/>.
    /// </remarks>
    public sealed class AIToolMcpAdapter
    {
        private readonly McpServerOptions options;
        private readonly AIToolExternalCatalog catalog;
        private readonly AIToolExternalExecutor executor;

        /// <summary>
        /// Initializes a new instance of the <see cref="AIToolMcpAdapter"/> class.
        /// </summary>
        public AIToolMcpAdapter(McpServerOptions options)
            : this(options, () => AIToolManager.GetTools(), call => call.Exec())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AIToolMcpAdapter"/> class with
        /// injectable tool source and executor. Used by tests and embedding scenarios
        /// that want to substitute the catalog or execution pipeline.
        /// </summary>
        public AIToolMcpAdapter(
            McpServerOptions options,
            Func<IReadOnlyDictionary<string, AITool>> toolSource,
            Func<AIToolCall, Task<AIReturn>> executor)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.catalog = new AIToolExternalCatalog(toolSource ?? throw new ArgumentNullException(nameof(toolSource)));
            this.executor = new AIToolExternalExecutor(
                this.catalog,
                executor ?? throw new ArgumentNullException(nameof(executor)));
        }

        /// <summary>
        /// Builds the list of MCP tool descriptors to expose, applying the configured
        /// allow-list and per-tool mutability filter.
        /// </summary>
        public IReadOnlyList<McpToolDescriptor> BuildDescriptors()
        {
            return this.catalog.BuildDescriptors(this.CreateCatalogOptions())
                .Select(tool => new McpToolDescriptor(
                    tool.Name,
                    tool.Description,
                    tool.InputSchema,
                    tool.Tags,
                    tool.OutputSchema,
                    tool.Annotations))
                .ToList();
        }

        /// <summary>
        /// Returns whether the named tool is currently exposed (allow-list + per-tool mutability).
        /// </summary>
        public bool IsExposed(string toolName)
        {
            return this.catalog.IsExposed(toolName, this.CreateCatalogOptions());
        }

        /// <summary>
        /// Executes a tool via <see cref="AIToolCall.Exec(System.Threading.CancellationToken)"/>
        /// and returns the JSON payload to embed in the MCP <c>tools/call</c> response.
        /// </summary>
        /// <param name="toolName">Tool name.</param>
        /// <param name="arguments">JSON object containing tool arguments.</param>
        /// <param name="cancellationToken">Token forwarded to the tool execution pipeline.</param>
        public async Task<McpToolCallResult> ExecuteAsync(
            string toolName,
            JObject? arguments,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return McpToolCallResult.Error("Tool name is required");
            }

            if (!this.IsExposed(toolName))
            {
                return McpToolCallResult.Error($"Tool '{toolName}' is not exposed via MCP");
            }

            var result = await this.executor.ExecuteAsync(
                toolName,
                arguments,
                this.CreateCatalogOptions(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.IsError
                ? McpToolCallResult.Error(result.ErrorMessage ?? $"Tool '{toolName}' failed")
                : McpToolCallResult.Ok(result.Payload, result.Images);
        }

        private AIToolExternalCatalogOptions CreateCatalogOptions()
        {
            return new AIToolExternalCatalogOptions
            {
                EnabledTools = this.options.EnabledTools,
                ExposeMutatingTools = this.options.ExposeMutatingTools,
                AllowListOverridesMutatingFilter = true,
            };
        }
    }
}
