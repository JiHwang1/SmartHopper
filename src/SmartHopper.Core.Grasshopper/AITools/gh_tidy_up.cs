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
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GhJSON.Core.DependencyGraph;
using GhJSON.Core.SchemaModels;
using GhJSON.Grasshopper;
using GhJSON.Grasshopper.LayoutRefinements;
using GhJSON.Grasshopper.Serialization;
using Grasshopper;
using Newtonsoft.Json.Linq;
using Rhino;
using SmartHopper.Core.Grasshopper.Utils.Canvas;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.Core.Returns;
using SmartHopper.Infrastructure.AICall.Tools;
using SmartHopper.Infrastructure.AITools;

namespace SmartHopper.Core.Grasshopper.AITools
{
    /// <summary>
    /// Tool provider for arranging Grasshopper components by data flow.
    /// </summary>
    public class gh_tidy_up : IAIToolProvider
    {
        /// <summary>
        /// Name of the AI tool provided by this class.
        /// </summary>
        private readonly string toolName = "gh_tidy_up";

        /// <summary>
        /// Returns AI tools for arranging components.
        /// </summary>
        /// <returns></returns>
        public IEnumerable<AITool> GetTools()
        {
            yield return new AITool(
                name: this.toolName,
                description: "Automatically arrange components into a clean grid layout respecting data flow direction. Organizes components left-to-right based on their connections. Use this to clean up messy definitions. Requires component GUIDs from gh_get.",
                category: "Components",
                parametersSchema: @"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""guids"": {
                            ""type"": ""array"",
                            ""items"": { ""type"": ""string"" },
                            ""description"": ""List of component GUIDs to include in the tidy-up.""
                        },
                        ""startPoint"": {
                            ""type"": ""object"",
                            ""properties"": {
                                ""x"": { ""type"": ""number"" },
                                ""y"": { ""type"": ""number"" }
                            },
                            ""description"": ""Optional absolute start point for the top-left of the grid. Overrides selection's top-left if provided.""
                        },
                        ""viewportOnly"": {
                            ""type"": ""boolean"",
                            ""default"": false,
                            ""description"": ""When true, only includes components currently visible in the canvas viewport. Ignores off-screen components even if their GUIDs are provided.""
                        }
                    },
                    ""required"": [ ""guids"" ]
                }",
                execute: this.GhTidyUpAsync,
                mutatesCanvas: true,
                tags: new[] { "canvas", "components", "mutating", "layout" },
                outputSchema: @"{ ""type"": ""object"", ""properties"": { ""success"": { ""type"": ""boolean"" }, ""moved"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Instance GUIDs of components that were moved."" }, ""affectedGuids"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Instance GUIDs of components that were moved (alias for moved)."" } } }",
                annotations: new AIToolAnnotations(destructiveHint: false));

            // Specialized wrapper: gh_tidy_up_selected
            yield return new AITool(
                name: "gh_tidy_up_selected",
                description: "Organize currently selected components into a tidy grid layout. Quick way to clean up selected items without needing to specify GUIDs manually. Arranges components left-to-right based on connections.",
                category: "Components",
                parametersSchema: @"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""startPoint"": {
                            ""type"": ""object"",
                            ""properties"": {
                                ""x"": { ""type"": ""number"" },
                                ""y"": { ""type"": ""number"" }
                            },
                            ""description"": ""Optional absolute start point for the top-left of the grid. Overrides selection's top-left if provided.""
                        },
                        ""viewportOnly"": {
                            ""type"": ""boolean"",
                            ""default"": false,
                            ""description"": ""When true, only includes selected components currently visible in the canvas viewport.""
                        }
                    }
                }",
                execute: this.GhTidyUpSelectedAsync,
                mutatesCanvas: true,
                tags: new[] { "canvas", "components", "mutating", "layout" },
                outputSchema: @"{ ""type"": ""object"", ""properties"": { ""success"": { ""type"": ""boolean"" }, ""moved"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Instance GUIDs of components that were moved."" }, ""affectedGuids"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Instance GUIDs of components that were moved (alias for moved)."" } } }",
                annotations: new AIToolAnnotations(destructiveHint: false));
        }

        private Task<AIReturn> GhTidyUpAsync(AIToolCall toolCall) => this.TidyUpAsync(toolCall, false);

        private Task<AIReturn> GhTidyUpSelectedAsync(AIToolCall toolCall) => this.TidyUpAsync(toolCall, true);

        private async Task<AIReturn> TidyUpAsync(AIToolCall toolCall, bool selectedOnly)
        {
            var output = new AIReturn { Request = toolCall };
            toolCall.SkipMetricsValidation = true;
            try
            {
                var token = toolCall.CancellationToken;
                var toolInfo = toolCall.GetToolCall();
                var args = toolInfo.GetArgumentsOrEmpty();
                var requested = args["guids"]?.ToObject<List<string>>()
                    .Select(text => Guid.TryParse(text, out var id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty).ToHashSet();
                var startToken = args["startPoint"];
                var hasStart = startToken != null;
                var origin = hasStart
                    ? new PointF(startToken["x"]?.ToObject<float>() ?? 0f, startToken["y"]?.ToObject<float>() ?? 0f)
                    : default;
                var viewportOnly = args["viewportOnly"]?.ToObject<bool>() ?? false;
                var canvasDocument = await OnUiThreadAsync(CanvasAccess.GetCurrentCanvas, token).ConfigureAwait(false);
                var snapshot = await CanvasAccess.InvokeInDocumentAsync(canvasDocument, token, () =>
                {
                    // Native selection, bounds, and serialization are confined to the UI thread.
                    var selected = CanvasAccess.GetCurrentObjects().Where(item => requested != null
                        ? requested.Contains(item.InstanceGuid)
                        : selectedOnly && item.Attributes?.Selected == true).ToList();
                    var viewport = Instances.ActiveCanvas?.Viewport;
                    if (viewportOnly && viewport != null)
                    {
                        var visibleRegion = viewport.VisibleRegion;
                        selected = selected.Where(item => item.Attributes != null &&
                            visibleRegion.IntersectsWith(item.Attributes.Bounds)).ToList();
                    }

                    if (selected.Count == 0)
                    {
                        throw new InvalidOperationException(selectedOnly
                            ? "No selected components are visible in the current viewport."
                            : "No matching components found.");
                    }

                    var serialized = GhJsonGrasshopper.Serialize(selected, SerializationOptions.Default);
                    var pivots = selected.ToDictionary(item => item.InstanceGuid, item => item.Attributes.Pivot);
                    return Task.FromResult((Document: serialized, Layout: CopyLayoutGraph(serialized), Pivots: pivots));
                }).ConfigureAwait(false);

                // Only the detached graph of IDs, integer pivots, and connection endpoints enters Task.Run.
                // GhJSON.Core 1.1.1 CalculateLayout has no Rhino/Grasshopper assembly dependencies.
                var layout = await CalculateLayoutAsync(snapshot.Layout, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var placement = await CanvasAccess.InvokeInDocumentAsync(canvasDocument, token, async () =>
                {
                    if (snapshot.Pivots.Keys.Any(id => canvasDocument.FindObject(id, false) == null))
                    {
                        throw new InvalidOperationException("A layout target was removed while layout was being calculated.");
                    }

                    // Refinements resolve native bounds and ports: they must remain on the UI thread.
                    var positions = LayoutRefinementEngine.ApplyRefinements(layout, snapshot.Document,
                        new LayoutRefinementOptions
                        {
                            SpacingX = 200f,
                            SpacingY = 100f,
                            ApplyBoundsAwareSpacing = true,
                            AlignParamsToInputPorts = true,
                            AlignOneToOneConnections = true,
                            MinimizeConnectionLengths = true,
                            AvoidCollisions = true,
                        });
                    if (positions.Count == 0)
                    {
                        throw new InvalidOperationException("Layout produced no positions for the selected components.");
                    }

                    if (!hasStart)
                    {
                        var first = positions.OrderBy(item => item.Value.X).ThenBy(item => item.Value.Y).First();
                        var pivot = snapshot.Pivots[first.Key];
                        origin = new PointF(pivot.X - first.Value.X, pivot.Y - first.Value.Y);
                    }

                    var targets = positions.ToDictionary(item => item.Key,
                        item => new PointF(origin.X + item.Value.X, origin.Y + item.Value.Y));
                    var moved = await CanvasAccess.MoveInstancesAsync(targets, false, true, token, canvasDocument).ConfigureAwait(true);
                    return (Moved: moved, Targets: targets);
                }).ConfigureAwait(false);

                var result = new JObject
                {
                    ["moved"] = JArray.FromObject(placement.Moved.Select(id => id.ToString())),
                    ["targets"] = new JObject(placement.Targets.Select(item => new JProperty(item.Key.ToString("D"),
                        new JObject { ["x"] = item.Value.X, ["y"] = item.Value.Y }))),
                };
                var body = AIBodyBuilder.Create()
                    .AddToolResult(result, id: toolInfo.Id, name: toolInfo.Name ?? this.toolName)
                    .Build();
                output.CreateSuccess(body, toolCall);
            }
            catch (Exception ex)
            {
                output.CreateError($"Error: {ex.Message}");
            }

            return output;
        }

        private static GhJsonDocument CopyLayoutGraph(GhJsonDocument document)
        {
            // Do not pass serialized extension dictionaries or parameter data to the worker.
            // goinmul: Core 1.1.1 reads component Id/InstanceGuid/Pivot and endpoint Id/ParamIndex only.
            // Recheck this field set before upgrading GhJSON.Core.
            return new GhJsonDocument(document.Schema, null,
                document.Components.Select(component => new GhJsonComponent
                {
                    Id = component.Id,
                    InstanceGuid = component.InstanceGuid,
                    Pivot = component.Pivot == null ? null : new GhJsonPivot { X = component.Pivot.X, Y = component.Pivot.Y },
                }),
                document.Connections?.Select(connection => new GhJsonConnection
                {
                    From = new GhJsonConnectionEndpoint { Id = connection.From.Id, ParamIndex = connection.From.ParamIndex },
                    To = new GhJsonConnectionEndpoint { Id = connection.To.Id, ParamIndex = connection.To.ParamIndex },
                }), null);
        }

        private static Task<LayoutResult> CalculateLayoutAsync(GhJsonDocument graph, CancellationToken cancellationToken)
        {
            return Task.Run(() => GhJSON.Core.GhJson.CalculateLayout(graph, new LayoutOptions
            {
                SpacingX = 200f,
                SpacingY = 100f,
                IslandSpacingY = 150f,
            }), cancellationToken);
        }

        private static Task<T> OnUiThreadAsync<T>(Func<T> action, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Run()
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    completion.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }

            if (RhinoApp.InvokeRequired) RhinoApp.InvokeOnUiThread((Action)Run);
            else Run();
            return completion.Task;
        }
    }
}
