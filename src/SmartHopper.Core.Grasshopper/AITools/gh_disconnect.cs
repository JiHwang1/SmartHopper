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
using System.Threading.Tasks;
using GhJSON.Grasshopper;
using Grasshopper;
using Grasshopper.Kernel;
using Newtonsoft.Json.Linq;
using SmartHopper.Core.Grasshopper.Utils.Canvas;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.Core.Returns;
using SmartHopper.Infrastructure.AICall.Tools;
using SmartHopper.Infrastructure.AITools;

namespace SmartHopper.Core.Grasshopper.AITools
{
    /// <summary>
    /// Tool provider for disconnecting Grasshopper components.
    /// Removes wires between component parameters on the canvas.
    /// </summary>
    public class gh_disconnect : IAIToolProvider
    {
        /// <summary>
        /// Name of the AI tool provided by this class.
        /// </summary>
        private readonly string toolName = "gh_disconnect";

        /// <summary>
        /// Returns the GH disconnect tool.
        /// </summary>
        public IEnumerable<AITool> GetTools()
        {
            yield return new AITool(
                name: this.toolName,
                description: "Disconnect Grasshopper components by removing wires between outputs and inputs. Use this to break data flow between existing components on the canvas. Requires component GUIDs (use gh_get_selected or gh_get to find them first).",
                category: "Components",
                parametersSchema: @"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""connections"": {
                            ""type"": ""array"",
                            ""description"": ""Array of disconnection specifications"",
                            ""items"": {
                                ""type"": ""object"",
                                ""properties"": {
                                    ""sourceGuid"": {
                                        ""type"": ""string"",
                                        ""description"": ""GUID of the source component (output side)""
                                    },
                                    ""sourceParam"": {
                                        ""type"": ""string"",
                                        ""description"": ""Name or nickname of the output parameter. If not specified, uses the first output.""
                                    },
                                    ""targetGuid"": {
                                        ""type"": ""string"",
                                        ""description"": ""GUID of the target component (input side)""
                                    },
                                    ""targetParam"": {
                                        ""type"": ""string"",
                                        ""description"": ""Name or nickname of the input parameter. If not specified, uses the first input.""
                                    }
                                },
                                ""required"": [""sourceGuid"", ""targetGuid""]
                            }
                        }
                    },
                    ""required"": [""connections""]
                }",
                execute: this.GhDisconnectToolAsync,
                mutatesCanvas: true,
                enabled: true,
                tags: new[] { "canvas", "components", "mutating", "connections", "disconnect" },
                outputSchema: @"{ ""type"": ""object"", ""properties"": { ""successful"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } }, ""failed"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } }, ""successCount"": { ""type"": ""integer"" }, ""failCount"": { ""type"": ""integer"" } } }",
                annotations: new AIToolAnnotations(destructiveHint: true));
        }

        /// <summary>
        /// Executes the GH disconnect tool.
        /// </summary>
        private async Task<AIReturn> GhDisconnectToolAsync(AIToolCall toolCall)
        {
            return await CanvasReviewedTools.ConnectionsAsync(toolCall, disconnect: true).ConfigureAwait(false);
        }
    }
}
