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
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GhJSON.Core;
using GhJSON.Core.SchemaModels;
using GhJSON.Grasshopper;
using GhJSON.Grasshopper.PutOperations;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SmartHopper.Core.Grasshopper.Utils.Canvas;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.Core.Returns;
using SmartHopper.Infrastructure.AICall.Tools;
using SmartHopper.Infrastructure.AITools;
using SmartHopper.Infrastructure.Diagnostics;
using SmartHopper.Infrastructure.Dialogs;

namespace SmartHopper.Core.Grasshopper.AITools
{
    /// <summary>
    /// Tool provider for placing Grasshopper components from GhJSON format.
    /// </summary>
    public class gh_put : IAIToolProvider
    {
        /// <summary>
        /// Name of the AI tool provided by this class.
        /// </summary>
        private readonly string toolName = "gh_put";

        /// <summary>
        /// Returns the GH put tool.
        /// </summary>
        /// <returns></returns>
        public IEnumerable<AITool> GetTools()
        {
            yield return new AITool(
                name: this.toolName,
                description: "Add or update components from GhJSON. Matching instanceGuids update existing objects, preserving identity, omitted positions, and external wires. Rejected updates are skipped. Connections reference IDs within this GhJSON document; use gh_connect for other canvas objects. Use this to create component networks, add missing components, or build parametric definitions. The GhJSON must include component types, positions, and connections. Component-specific state (e.g. Number Slider values under componentState.extensions['gh.numberslider'].value using the format 'current<min~max>', Panel text under componentState.extensions['gh.panel'].text) is preserved. Example: gh_put({ ghjson: '...' }) or gh_put({ ghjson: 'C:/path/to/file.ghjson' }). See also: gh_get, script_generate_and_place_on_canvas.",
                category: "Components",
                parametersSchema: @"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""ghjson"": { ""type"": ""string"", ""description"": ""GhJSON document string, or an absolute file path to a .ghjson file containing the document."" },
                        ""editMode"": { ""type"": ""boolean"", ""description"": ""Compatibility hint for autoOffset. Matching instanceGuids are always reviewed as updates."" },
                        ""autoOffset"": { ""type"": ""boolean"", ""default"": true, ""description"": ""When true, newly placed components are offset on the canvas so they do not overlap existing objects. Defaults to false when updating existing objects or editMode is true."" }
                    },
                    ""required"": [""ghjson""]
                }",
                execute: this.GhPutToolAsync,
                mutatesCanvas: true,
                tags: new[] { "canvas", "components", "mutating", "ghjson" },
                outputSchema: @"{ ""type"": ""object"", ""properties"": { ""components"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Names of the placed or replaced components."" }, ""instanceGuids"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Instance GUIDs of the placed or replaced components."" }, ""acceptedChanges"": { ""type"": ""integer"", ""description"": ""Changes actually applied."" }, ""alreadySatisfiedChanges"": { ""type"": ""integer"" }, ""rejectedChanges"": { ""type"": ""integer"", ""description"": ""Rejected, protected, or failed changes."" }, ""createdInstanceGuids"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }, ""updatedInstanceGuids"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }, ""analysis"": { ""type"": [""string"", ""null""], ""description"": ""Validation, error, or warning summary. Null when nothing notable happened."" } } }",
                annotations: new AIToolAnnotations(destructiveHint: false));
        }

        private async Task<AIReturn> GhPutToolAsync(AIToolCall toolCall)
        {
            var output = new AIReturn { Request = toolCall };
            toolCall.SkipMetricsValidation = true;
            try
            {
                var info = toolCall.GetToolCall();
                var args = info.GetArgumentsOrEmpty();
                // Parse before prompts or native mutations.
                var document = ParseDocument(ExtractGhJsonString(args["ghjson"]));
                var token = toolCall.CancellationToken;
                var expectedDocument = await CanvasAccess.GetCurrentCanvasAsync(token).ConfigureAwait(false);
                var rejected = new HashSet<Guid>();
                var unchanged = new HashSet<Guid>();
                var warnings = new List<string>();
                var baseline = await CanvasAccess.InvokeInDocumentAsync(expectedDocument, token, () =>
                {
                    var protectedIds = CanvasProtection.GetProtectedInstanceGuids();
                    var existing = document.Components
                        .Where(c => c.InstanceGuid.HasValue && expectedDocument.FindObject(c.InstanceGuid.Value, false) != null)
                        .ToDictionary(c => c.InstanceGuid.Value, c => expectedDocument.FindObject(c.InstanceGuid.Value, false));
                    foreach (var c in document.Components)
                    {
                        if (c.InstanceGuid.HasValue && protectedIds.Contains(c.InstanceGuid.Value)) rejected.Add(c.InstanceGuid.Value);
                        if (c.Pivot == null && c.InstanceGuid.HasValue && existing.TryGetValue(c.InstanceGuid.Value, out var obj))
                        {
                            c.Pivot = GhJsonPivot.FromPointF(obj.Attributes.Pivot);
                        }
                    }

                    var original = GhJsonGrasshopper.GetByGuids(existing.Keys.ToList());
                    foreach (var c in document.Components.Where(c => c.InstanceGuid.HasValue && existing.ContainsKey(c.InstanceGuid.Value)))
                    {
                        var previous = original.Components.FirstOrDefault(x => x.InstanceGuid == c.InstanceGuid);
                        if (previous != null && ComponentsAreEqual(c, previous)) unchanged.Add(c.InstanceGuid.Value);
                    }

                    return Task.FromResult((Objects: existing, Original: original));
                }).ConfigureAwait(false);

                var requestedCount = document.Components.Count + (document.Connections?.Count ?? 0) + (document.Groups?.Count ?? 0);
                // Resolve missing positions and optional offset before review, so preview and application agree.
                if (document.Components.Any(c => c.Pivot == null))
                {
                    var layout = GhJson.ReorganizePivots(document);
                    foreach (var component in document.Components.Where(c => c.Pivot == null))
                        component.Pivot = layout.Components.First(c => c.Id == component.Id).Pivot;
                }
                var requestedAutoOffset = args["autoOffset"]?.ToObject<bool>() ??
                    (!(args["editMode"]?.ToObject<bool>() ?? false) && baseline.Objects.Count == 0);
                if (requestedAutoOffset && baseline.Objects.Count == 0)
                {
                    await CanvasAccess.InvokeInDocumentAsync(expectedDocument, token, () =>
                    {
                        var bounds = expectedDocument.Objects.Where(o => o.Attributes != null).Select(o => o.Attributes.Bounds).ToList();
                        if (bounds.Count > 0)
                        {
                            var dx = (int)Math.Ceiling(bounds.Max(b => b.Right) + 100) - document.Components.Min(c => c.Pivot.X);
                            var dy = (int)Math.Floor(bounds.Min(b => b.Top)) - document.Components.Min(c => c.Pivot.Y);
                            foreach (var component in document.Components) component.Pivot = new GhJsonPivot(component.Pivot.X + dx, component.Pivot.Y + dy);
                        }
                        return Task.FromResult(true);
                    }).ConfigureAwait(false);
                }
                var reviewPlan = GhPutChangePlan.Create(document, baseline.Original);
                reviewPlan.Session.DocumentId = expectedDocument.DocumentID;
                if (reviewPlan.Session.Items.Count > 0)
                {
                    await CanvasChangeReviewService.ReviewAsync(reviewPlan.Session, toolCall.InvocationContext, token).ConfigureAwait(false);
                }
                foreach (var component in document.Components.Where(c => c.InstanceGuid.HasValue && !unchanged.Contains(c.InstanceGuid.Value)))
                {
                    var item = reviewPlan.Session.Items.FirstOrDefault(i => i.ComponentId == component.Id &&
                        (i.Kind == CanvasChangeKind.ComponentAdded || i.Kind == CanvasChangeKind.ComponentModified));
                    if (item == null || !reviewPlan.Session.IsEffectivelyAccepted(item)) rejected.Add(component.InstanceGuid.Value);
                }
                // Preserve unchanged endpoints so accepted wires can connect to live objects.
                document = new GhJsonDocument(document.Schema, document.Metadata, document.Components,
                    document.Connections?.Where((_, index) => reviewPlan.Session.Items.Any(i => i.ConnectionIndex == index && reviewPlan.Session.IsEffectivelyAccepted(i))).ToList(),
                    document.Groups?.Where((_, index) => reviewPlan.Session.Items.Any(i => i.GroupIndex == index && reviewPlan.Session.IsEffectivelyAccepted(i))).ToList());

                var autoOffset = false; // Positions have already been reviewed.
                var applied = await CanvasAccess.InvokeInDocumentAsync(expectedDocument, token, () =>
                {
                    using var undo = new CanvasUndoBatch(expectedDocument);
                    var protectedNow = CanvasProtection.GetProtectedInstanceGuids();
                    if (document.Components.Any(c => c.InstanceGuid.HasValue && !rejected.Contains(c.InstanceGuid.Value) && protectedNow.Contains(c.InstanceGuid.Value)))
                        throw new InvalidOperationException("A requested object became protected. Retry the update.");
                    var current = GhJsonGrasshopper.GetByGuids(baseline.Objects.Keys.ToList());
                    foreach (var pair in baseline.Objects)
                    {
                        var oldState = baseline.Original.Components.FirstOrDefault(c => c.InstanceGuid == pair.Key);
                        var newState = current.Components.FirstOrDefault(c => c.InstanceGuid == pair.Key);
                        if (!ReferenceEquals(expectedDocument.FindObject(pair.Key, false), pair.Value) || oldState == null || newState == null || !ComponentsAreEqual(oldState, newState))
                            throw new InvalidOperationException("A requested object changed while approval was pending. Retry the update.");
                    }

                    var components = document.Components.Where(c => !c.InstanceGuid.HasValue ||
                        (!rejected.Contains(c.InstanceGuid.Value) && !unchanged.Contains(c.InstanceGuid.Value))).ToList();
                    // Never remove an existing object for a replacement that cannot be instantiated.
                    foreach (var c in components.ToList())
                    {
                        if (c.InstanceGuid.HasValue && baseline.Objects.ContainsKey(c.InstanceGuid.Value) && !GhJsonGrasshopper.CanInstantiate(c))
                        {
                            rejected.Add(c.InstanceGuid.Value);
                            components.Remove(c);
                            warnings.Add($"Replacement '{c.Name}' is unavailable; the original object was retained.");
                        }
                    }

                    var replacements = components.Where(c => c.InstanceGuid.HasValue && baseline.Objects.ContainsKey(c.InstanceGuid.Value))
                        .Select(c => baseline.Objects[c.InstanceGuid.Value]).ToList();
                    var wires = CaptureAffectedWires(expectedDocument, replacements.Select(o => o.InstanceGuid).ToHashSet());
                    var placementIds = components.Select(c => c.InstanceGuid.Value).ToHashSet();
                    PutResult result;
                    try
                    {
                        if (replacements.Count > 0)
                        {
                            expectedDocument.UndoUtil.RecordRemoveObjectEvent("[SH] Update components", replacements);
                            foreach (var obj in replacements)
                            {
                                obj.IsolateObject();
                                expectedDocument.RemoveObject(obj, false);
                            }
                        }

                        result = components.Count == 0 ? new PutResult { Success = true } : GhJsonGrasshopper.Put(
                            new GhJsonDocument(document.Schema, document.Metadata, components, null, null),
                            new PutOptions { RegenerateInstanceGuids = false, CreateConnections = false, CreateGroups = false,
                                SelectPlacedObjects = true, SkipInvalidComponents = true, AutoOffset = autoOffset });
                        if (!result.Success) throw new InvalidOperationException(result.ErrorMessage ?? "Placement failed.");
                    }
                    catch
                    {
                        foreach (var obj in expectedDocument.Objects.Where(o => placementIds.Contains(o.InstanceGuid)).ToList())
                        {
                            obj.IsolateObject();
                            expectedDocument.RemoveObject(obj, false);
                        }

                        foreach (var obj in replacements) expectedDocument.AddObject(obj, false);
                        RestoreOriginalWires(wires, expectedDocument, warnings);
                        if (replacements.All(obj => ReferenceEquals(expectedDocument.FindObject(obj.InstanceGuid, false), obj)) &&
                            !warnings.Any(warning => warning.Contains("could not be restored", StringComparison.Ordinal)))
                            undo.DiscardRestoredChanges();
                        throw;
                    }

                    foreach (var original in replacements.Where(o => expectedDocument.FindObject(o.InstanceGuid, false) == null))
                    {
                        expectedDocument.AddObject(original, false);
                        rejected.Add(original.InstanceGuid);
                        warnings.Add($"Replacement failed for '{original.Name}'; the original object was restored.");
                    }

                    RestoreOriginalWires(wires, expectedDocument, warnings);
                    var idMap = document.Components.Where(c => c.Id.HasValue && c.InstanceGuid.HasValue &&
                            !rejected.Contains(c.InstanceGuid.Value) && expectedDocument.FindObject(c.InstanceGuid.Value, false) != null)
                        .ToDictionary(c => c.Id.Value, c => c.InstanceGuid.Value);
                    foreach (var pair in result.IdToGuidMapping) idMap[pair.Key] = pair.Value;
                    var connections = 0;
                    var satisfied = unchanged.Count(id => !rejected.Contains(id));
                    foreach (var connection in document.Connections ?? new List<GhJsonConnection>())
                    {
                        if (TryConnect(connection, idMap, expectedDocument, out var changed))
                        {
                            if (changed) connections++; else satisfied++;
                        }
                        else warnings.Add("A requested document connection could not be applied.");
                    }

                    var groups = 0;
                    var applicationFailed = false;
                    foreach (var definition in document.Groups ?? new List<GhJsonGroup>())
                    {
                        try
                        {
                            if (definition.Members == null || definition.Members.Count == 0 || definition.Members.Any(id => !idMap.ContainsKey(id)))
                            {
                                warnings.Add("A requested group was skipped because not all members were applied.");
                                continue;
                            }

                            var members = definition.Members.Select(id => idMap[id]).ToHashSet();
                            var existingGroup = definition.InstanceGuid.HasValue ? expectedDocument.FindObject(definition.InstanceGuid.Value, false) as GH_Group : null;
                            var sameColor = existingGroup != null && (string.IsNullOrWhiteSpace(definition.Color) ||
                                definition.Color.Replace(" ", string.Empty) == $"{existingGroup.Colour.A},{existingGroup.Colour.R},{existingGroup.Colour.G},{existingGroup.Colour.B}");
                            if (existingGroup != null && members.SetEquals(existingGroup.ObjectIDs) && existingGroup.NickName == (definition.Name ?? string.Empty) && sameColor)
                            {
                                satisfied++;
                                continue;
                            }

                            // Existing groups are kept intact; changing their membership is a separate operation.
                            if (definition.InstanceGuid.HasValue && expectedDocument.FindObject(definition.InstanceGuid.Value, false) != null)
                            {
                                warnings.Add("An existing group was retained; use gh_group for membership changes.");
                                continue;
                            }

                            var group = new GH_Group { NickName = definition.Name ?? string.Empty };
                            group.CreateAttributes();
                            if (definition.InstanceGuid.HasValue) group.NewInstanceGuid(definition.InstanceGuid.Value);
                            if (!string.IsNullOrWhiteSpace(definition.Color))
                            {
                                var parts = definition.Color.Split(',');
                                if (parts.Length == 4 && parts.All(part => byte.TryParse(part, out _)))
                                    group.Colour = Color.FromArgb(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
                            }

                            foreach (var member in members) group.AddObject(member);
                            expectedDocument.AddObject(group, false);
                            groups++;
                            expectedDocument.UndoUtil.RecordAddObjectEvent("[SH] Add group", new[] { group });
                        }
                        catch (Exception ex)
                        {
                            applicationFailed = true;
                            warnings.Add("A requested group could not be fully applied: " + ex.Message);
                        }
                    }

                    try
                    {
                        if (result.PlacedObjects.Count > 0 || connections > 0 || groups > 0)
                        {
                            expectedDocument.NewSolution(false);
                            Instances.RedrawCanvas();
                        }
                    }
                    catch (Exception ex)
                    {
                        applicationFailed = true;
                        warnings.Add("Canvas refresh failed after applying changes: " + ex.Message);
                    }
                    var actual = result.PlacedObjects.Where(obj => ReferenceEquals(expectedDocument.FindObject(obj.InstanceGuid, false), obj)).ToList();
                    var planned = requestedCount;
                    var acceptedCount = actual.Count + connections + groups;
                    var unapplied = Math.Max(0, planned - acceptedCount - satisfied);
                    if (warnings.Any(w => w.Contains("could not be restored", StringComparison.Ordinal))) applicationFailed = true;
                    return Task.FromResult((Placed: actual, Accepted: acceptedCount, Satisfied: satisfied,
                        Rejected: unapplied, Failed: applicationFailed, Warnings: result.Warnings.Concat(warnings).Concat(result.FailedComponents).ToList()));
                }).ConfigureAwait(false);

                var ids = applied.Placed.Select(obj => obj.InstanceGuid).ToList();
                var payload = new JObject
                {
                    ["components"] = JArray.FromObject(applied.Placed.Select(obj => obj.Name)),
                    ["instanceGuids"] = JArray.FromObject(ids),
                    ["createdInstanceGuids"] = JArray.FromObject(ids.Where(id => !baseline.Objects.ContainsKey(id))),
                    ["updatedInstanceGuids"] = JArray.FromObject(ids.Where(id => baseline.Objects.ContainsKey(id))),
                    ["unchangedInstanceGuids"] = JArray.FromObject(unchanged.Where(id => !rejected.Contains(id))),
                    ["acceptedChanges"] = applied.Accepted,
                    ["alreadySatisfiedChanges"] = applied.Satisfied,
                    ["rejectedChanges"] = applied.Rejected,
                    ["success"] = applied.Rejected == 0 && !applied.Failed,
                    ["failCount"] = applied.Failed ? 1 : 0,
                    ["analysis"] = applied.Warnings.Count == 0 ? null : string.Join("\n", applied.Warnings),
                };
                payload.WithEnvelope(ToolResultEnvelope.Create(info.Name ?? this.toolName, ToolResultContentType.Object, toolCallId: info.Id));
                if (applied.Rejected > 0 || applied.Failed) output.AddRuntimeMessage(SHRuntimeMessageSeverity.Warning, SHRuntimeMessageOrigin.Tool, "Some requested changes were not applied; inspect rejectedChanges and analysis.");
                output.CreateSuccess(AIBodyBuilder.Create().AddToolResult(payload, id: info.Id, name: info.Name ?? this.toolName).Build(), toolCall);
                return output;
            }
            catch (Exception ex)
            {
                output.CreateError("GhJSON placement failed: " + ex.Message);
                return output;
            }
        }

        /// <summary>Parses and validates a placement document before any native state is accessed.</summary>
        private static GhJsonDocument ParseDocument(string json)
        {
            var document = GhJson.FromJson(json);
            if (document?.Components == null || document.Components.Count == 0)
                throw new ArgumentException("GhJSON must contain a non-empty components array.");
            if (document.Components.Where(c => c.InstanceGuid.HasValue && c.InstanceGuid.Value != Guid.Empty)
                .GroupBy(c => c.InstanceGuid.Value).Any(group => group.Count() > 1))
                throw new ArgumentException("GhJSON contains duplicate instanceGuid values.");
            document = GhJson.Fix(document).Document;
            foreach (var component in document.Components)
                if (!component.InstanceGuid.HasValue || component.InstanceGuid.Value == Guid.Empty) component.InstanceGuid = Guid.NewGuid();
            if (!GhJson.IsValid(GhJson.ToJson(document), out var analysis))
                throw new ArgumentException("Invalid GhJSON: " + analysis);
            return document;
        }

        /// <summary>Resolves a document endpoint without guessing when an explicit port index is invalid.</summary>
        private static IGH_Param ResolvePort(IGH_DocumentObject obj, GhJsonConnectionEndpoint endpoint, bool input)
        {
            if (obj is IGH_Param param) return param;
            if (obj is not IGH_Component component) return null;
            var ports = input ? component.Params.Input : component.Params.Output;
            if (endpoint.ParamIndex.HasValue)
                return endpoint.ParamIndex.Value >= 0 && endpoint.ParamIndex.Value < ports.Count ? ports[endpoint.ParamIndex.Value] : null;
            return string.IsNullOrEmpty(endpoint.ParamName) ? ports.FirstOrDefault() :
                ports.FirstOrDefault(p => p.Name == endpoint.ParamName || p.NickName == endpoint.ParamName);
        }

        /// <summary>Applies one document-scoped wire, counting only a newly added source.</summary>
        private static bool TryConnect(GhJsonConnection connection, IDictionary<int, Guid> ids, GH_Document document, out bool changed)
        {
            changed = false;
            IGH_Param source = null;
            IGH_Param target = null;
            try
            {
                if (!ids.TryGetValue(connection.From.Id, out var sourceId) || !ids.TryGetValue(connection.To.Id, out var targetId)) return false;
                source = ResolvePort(document.FindObject(sourceId, false), connection.From, false);
                target = ResolvePort(document.FindObject(targetId, false), connection.To, true);
                if (source == null || target == null) return false;
                if (target.Sources.Contains(source)) return true;
                target.RecordUndoEvent("[SH] Connect components");
                target.AddSource(source);
                changed = target.Sources.Contains(source);
                return changed;
            }
            catch
            {
                // AddSource can notify listeners before they throw; report the observed wire state.
                try { changed = source != null && target?.Sources.Contains(source) == true; }
                catch { changed = false; }
                return changed;
            }
        }

        /// <summary>Captures exact port indices for wires touching replaced objects, including external wires.</summary>
        private static List<(Guid Source, int SourcePort, Guid Target, int TargetPort)> CaptureAffectedWires(GH_Document document, ISet<Guid> affected)
        {
            var outputs = new Dictionary<IGH_Param, (Guid Owner, int Port)>();
            foreach (var obj in document.Objects)
            {
                var ports = obj is IGH_Component component ? component.Params.Output : obj is IGH_Param param ? new List<IGH_Param> { param } : new List<IGH_Param>();
                for (var i = 0; i < ports.Count; i++) outputs[ports[i]] = (obj.InstanceGuid, i);
            }

            var wires = new List<(Guid, int, Guid, int)>();
            foreach (var obj in document.Objects)
            {
                var ports = obj is IGH_Component component ? component.Params.Input : obj is IGH_Param param ? new List<IGH_Param> { param } : new List<IGH_Param>();
                for (var i = 0; i < ports.Count; i++)
                    foreach (var source in ports[i].Sources)
                        if (outputs.TryGetValue(source, out var from) && (affected.Contains(from.Owner) || affected.Contains(obj.InstanceGuid)))
                            wires.Add((from.Owner, from.Port, obj.InstanceGuid, i));
            }

            return wires;
        }

        /// <summary>Restores original wires after replacement or rollback without ambiguous display-name matching.</summary>
        private static void RestoreOriginalWires(IEnumerable<(Guid Source, int SourcePort, Guid Target, int TargetPort)> wires, GH_Document document, ICollection<string> warnings)
        {
            foreach (var wire in wires)
            {
                var ids = new Dictionary<int, Guid> { [0] = wire.Source, [1] = wire.Target };
                var connection = new GhJsonConnection
                {
                    From = new GhJsonConnectionEndpoint { Id = 0, ParamIndex = wire.SourcePort },
                    To = new GhJsonConnectionEndpoint { Id = 1, ParamIndex = wire.TargetPort },
                };
                if (!TryConnect(connection, ids, document, out _)) warnings.Add("An original wire could not be restored.");
            }
        }

        /// <summary>
        /// Extracts the GhJSON string from the tool argument. Handles cases where the model
        /// passes the JSON as a string value, an object, or an array.
        /// </summary>
        /// <param name="token">The JToken containing the ghjson argument.</param>
        /// <returns>The GhJSON string, or an empty string if the argument is missing.</returns>
        private static string ExtractGhJsonString(JToken token)
        {
            if (token == null)
            {
                return string.Empty;
            }

            if (token.Type == JTokenType.Object || token.Type == JTokenType.Array)
            {
                return token.ToString(Formatting.None);
            }

            if (token.Type == JTokenType.String)
            {
                var value = token.Value<string>() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    try
                    {
                        var path = value.Trim();
                        if (File.Exists(path))
                        {
                            var extension = Path.GetExtension(path);
                            if (string.Equals(extension, ".ghjson", StringComparison.OrdinalIgnoreCase))
                            {
                                return File.ReadAllText(path);
                            }
                        }
                    }
                    catch
                    {
                        // Not a valid path; fall back to returning the literal value.
                    }
                }

                return value;
            }

            return token.ToString();
        }

        /// <summary>
        /// Compares two GhJSON component representations for structural equality,
        /// ignoring volatile fields such as runtime messages, IDs, and selection state.
        /// </summary>
        private static bool ComponentsAreEqual(GhJsonComponent incoming, GhJsonComponent existing)
        {
            var incomingJObj = JObject.FromObject(incoming);
            var existingJObj = JObject.FromObject(existing);

            // Remove volatile fields that should not affect equality
            incomingJObj.Remove("id");
            incomingJObj.Remove("errors");
            incomingJObj.Remove("warnings");
            incomingJObj.Remove("remarks");

            existingJObj.Remove("id");
            existingJObj.Remove("errors");
            existingJObj.Remove("warnings");
            existingJObj.Remove("remarks");

            // Remove selection state from componentState if present
            if (incomingJObj["componentState"] is JObject incomingState)
            {
                incomingState.Remove("selected");
            }

            if (existingJObj["componentState"] is JObject existingState)
            {
                existingState.Remove("selected");
            }

            // Remove runtime data from parameter settings. Runtime data is computed by the
            // canvas and is not part of the component's structural definition, so including
            // it makes every existing component look different from an incoming definition.
            static void RemoveRuntimeData(JObject jObj)
            {
                foreach (var settingsKey in new[] { "inputSettings", "outputSettings" })
                {
                    if (jObj[settingsKey] is JArray settingsArray)
                    {
                        foreach (var item in settingsArray)
                        {
                            if (item is JObject itemObj)
                            {
                                itemObj.Remove("runtimeData");
                            }
                        }
                    }
                }
            }

            RemoveRuntimeData(incomingJObj);
            RemoveRuntimeData(existingJObj);

            return JToken.DeepEquals(incomingJObj, existingJObj);
        }

    }
}
