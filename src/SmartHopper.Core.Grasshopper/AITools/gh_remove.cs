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
using Newtonsoft.Json.Linq;
using SmartHopper.Core.Grasshopper.Utils.Canvas;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.Core.Returns;
using SmartHopper.Infrastructure.AICall.Tools;
using SmartHopper.Infrastructure.AITools;
using SmartHopper.Infrastructure.Diagnostics;

namespace SmartHopper.Core.Grasshopper.AITools
{
    /// <summary>
    /// Tool provider for removing Grasshopper components by GUID.
    /// </summary>
    public class gh_remove : IAIToolProvider
    {
        /// <summary>
        /// Name of the AI tool provided by this class.
        /// </summary>
        private readonly string toolName = "gh_remove";

        /// <summary>
        /// Returns AI tools for removing components from the canvas.
        /// </summary>
        /// <returns>Collection of AI tools.</returns>
        public IEnumerable<AITool> GetTools()
        {
            yield return new AITool(
                name: this.toolName,
                description: "Remove components from the Grasshopper canvas by their instance GUIDs. The operation records an undo event so the user can reverse it with Ctrl+Z. Use GUIDs from gh_get or similar tools.",
                category: "Components",
                parametersSchema: @"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""instanceGuids"": {
                            ""type"": ""array"",
                            ""items"": { ""type"": ""string"", ""pattern"": ""^[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$"" },
                            ""description"": ""List of component instance GUIDs to remove.""
                        }
                    },
                    ""required"": [""instanceGuids""]
                }",
                execute: this.GhRemoveToolAsync,
                mutatesCanvas: true,
                tags: new[] { "canvas", "components", "mutating", "delete" },
                outputSchema: @"{ ""type"": ""object"", ""properties"": { ""removedGuids"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }, ""notFoundGuids"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } } }",
                annotations: new AIToolAnnotations(destructiveHint: true));
        }

        private Task<AIReturn> GhRemoveToolAsync(AIToolCall toolCall)
        {
            return CanvasReviewedTools.RemoveAsync(toolCall);
        }
    }
}
