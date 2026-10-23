/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Newtonsoft.Json.Linq;
using SmartHopper.Core.Grasshopper.Utils.Canvas;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.Core.Returns;
using SmartHopper.Infrastructure.AICall.Tools;
using SmartHopper.Infrastructure.AITools;
using SmartHopper.Infrastructure.Diagnostics;

namespace SmartHopper.Core.Grasshopper.AITools
{
    /// <summary>Provides reviewed component re-execution through a lock pulse.</summary>
    public class gh_run : IAIToolProvider
    {
        private readonly string toolName = "gh_run";

        public IEnumerable<AITool> GetTools()
        {
            yield return new AITool(
                name: this.toolName,
                description: "Re-run existing canvas objects by pulsing each target's lock state on the UI thread. The call is refused while a solution is in progress; locked and protected objects are skipped. The pulse is a net-zero state transition and creates no Undo record. Requires GUIDs from gh_get or gh_document_read.",
                category: "Components",
                parametersSchema: @"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""instanceGuids"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Instance GUIDs to re-run."" },
                        ""documentId"": { ""type"": [""string"", ""null""], ""description"": ""Optional active-document identity check from gh_document_list. A non-active document is refused."" }
                    },
                    ""required"": [""instanceGuids""]
                }",
                execute: this.GhRunAsync,
                mutatesCanvas: true,
                tags: new[] { "canvas", "components", "mutating", "execute" },
                outputSchema: @"{ ""type"": ""object"", ""properties"": { ""runGuids"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }, ""skippedGuids"": { ""type"": ""object"" }, ""protectedGuids"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }, ""rejectedGuids"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } } }",
                annotations: new AIToolAnnotations(destructiveHint: false));
        }

        private async Task<AIReturn> GhRunAsync(AIToolCall toolCall)
        {
            var output = new AIReturn { Request = toolCall };
            try
            {
                toolCall.SkipMetricsValidation = true;
                toolCall.CancellationToken.ThrowIfCancellationRequested();
                var info = toolCall.GetToolCall();
                var args = info.GetArgumentsOrEmpty();
                var requestedGuids = (args["instanceGuids"] as JArray)?.Values<string>()
                    .Select(value => Guid.TryParse(value, out var guid) && guid != Guid.Empty ? (Guid?)guid : null)
                    .OfType<Guid>()
                    .Distinct()
                    .ToList() ?? new List<Guid>();
                if (requestedGuids.Count == 0) throw new ArgumentException("Provide at least one valid 'instanceGuids' value.");
                var requestedDocumentId = args["documentId"]?.ToString();
                var token = toolCall.CancellationToken;
                var document = await CanvasAccess.GetCurrentCanvasAsync(token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("No active Grasshopper document.");
                if (!string.IsNullOrWhiteSpace(requestedDocumentId) &&
                    !string.Equals(document.DocumentID.ToString("D"), requestedDocumentId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The requested document is not active. Switch to it explicitly before running objects.");

                var prepared = await CanvasAccess.InvokeInDocumentAsync(document, token, () =>
                {
                    if (document.SolutionDepth > 0) throw new InvalidOperationException("Wait for the active solution to finish.");
                    var existing = requestedGuids.Where(id => GhValueTools.Find(document, id) != null).ToArray();
                    var protectedIds = CanvasProtection.FilterProtectedGuids(requestedGuids).Protected;
                    var allowed = existing.Except(protectedIds).ToArray();
                    var review = CanvasChangeReviewService.CreateComponentStateSession(
                        info.Name ?? this.toolName,
                        allowed,
                        "Re-run component");
                    review.DocumentId = document.DocumentID;
                    return Task.FromResult((Allowed: allowed, Protected: protectedIds.ToArray(),
                        Missing: requestedGuids.Except(existing).ToArray(), Review: review,
                        Signature: GhValueTools.Signature(document, allowed),
                        Objects: allowed.ToDictionary(id => id, id => GhValueTools.Find(document, id))));
                }).ConfigureAwait(false);

                if (prepared.Review.Items.Count > 0)
                {
                    await CanvasChangeReviewService.ReviewAsync(prepared.Review, toolCall.InvocationContext, token).ConfigureAwait(false);
                }
                var accepted = CanvasChangeReviewService.GetAcceptedComponentGuids(prepared.Review);

                var payload = await CanvasAccess.InvokeInDocumentAsync(document, token, () =>
                {
                    if (document.SolutionDepth > 0) throw new InvalidOperationException("A solution started during review; retry the run request.");
                    if (prepared.Signature != GhValueTools.Signature(document, prepared.Allowed) ||
                        prepared.Objects.Any(p => !ReferenceEquals(p.Value, GhValueTools.Find(document, p.Key))))
                        throw new InvalidOperationException("Reviewed run targets changed. Retry the run request.");
                    var run = new List<Guid>();
                    var skipped = new JObject();
                    foreach (var guid in prepared.Missing) skipped[guid.ToString("D")] = "Not present in the reviewed document.";
                    foreach (var guid in prepared.Protected) skipped[guid.ToString("D")] = "Protected object.";
                    foreach (var guid in prepared.Allowed.Where(id => !accepted.Contains(id))) skipped[guid.ToString("D")] = "Rejected in review.";

                    foreach (var guid in prepared.Allowed.Where(accepted.Contains))
                    {
                        var target = GhValueTools.Find(document, guid);
                        if (target is not IGH_ActiveObject active)
                        {
                            skipped[guid.ToString("D")] = "Object does not participate in solutions.";
                            continue;
                        }
                        if (CanvasProtection.IsProtected(guid))
                        {
                            skipped[guid.ToString("D")] = "Object became protected during review.";
                            continue;
                        }
                        if (active.Locked)
                        {
                            skipped[guid.ToString("D")] = "Object is locked.";
                            continue;
                        }

                        try
                        {
                            active.Locked = true;
                            active.Locked = false;
                        }
                        finally
                        {
                            if (active.Locked) active.Locked = false;
                        }
                        run.Add(guid);
                    }

                    if (run.Count > 0)
                    {
                        document.NewSolution(false);
                        Instances.RedrawCanvas();
                    }
                    return Task.FromResult(new JObject
                    {
                        ["runGuids"] = new JArray(run.Select(guid => guid.ToString("D"))),
                        ["skippedGuids"] = skipped,
                        ["protectedGuids"] = new JArray(prepared.Protected.Select(guid => guid.ToString("D"))),
                        ["rejectedGuids"] = new JArray(prepared.Allowed.Where(id => !accepted.Contains(id)).Select(id => id.ToString("D"))),
                    });
                }).ConfigureAwait(false);

                if (payload["runGuids"]!.Any() == false && prepared.Protected.Length == 0)
                {
                    output.AddRuntimeMessage(SHRuntimeMessageSeverity.Warning, SHRuntimeMessageOrigin.Tool,
                        "None of the requested objects could be re-run; inspect skippedGuids for the reasons.");
                }

                payload.WithEnvelope(ToolResultEnvelope.Create(info.Name ?? this.toolName, ToolResultContentType.Object));
                output.CreateSuccess(
                    AIBodyBuilder.Create().AddToolResult(payload, id: info.Id, name: info.Name ?? this.toolName).Build(),
                    toolCall);
            }
            catch (OperationCanceledException) when (toolCall.CancellationToken.IsCancellationRequested)
            {
                output.CreateError("Cancelled.");
            }
            catch (Exception ex)
            {
                output.CreateToolError(ex.Message, toolCall);
            }

            return output;
        }
    }
}
