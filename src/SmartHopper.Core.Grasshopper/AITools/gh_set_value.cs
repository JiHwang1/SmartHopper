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
using Newtonsoft.Json.Linq;
using SmartHopper.Core.Grasshopper.Utils.Canvas;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.Core.Returns;
using SmartHopper.Infrastructure.AICall.Tools;
using SmartHopper.Infrastructure.AITools;

namespace SmartHopper.Core.Grasshopper.AITools
{
    /// <summary>Provides the reviewed in-place value editing tool.</summary>
    public class gh_set_value : IAIToolProvider
    {
        private readonly string toolName = "gh_set_value";

        public IEnumerable<AITool> GetTools()
        {
            yield return new AITool(
                name: this.toolName,
                description: "Set an existing panel or scribble text, boolean toggle, number slider (clamped to its range), value-list selection, standalone persistent parameter, or a component input parameter's persistent default. Use param for a component input name, nickname, or zero-based index. The active document and target are revalidated after consent; the edit never follows a document switch. Requires GUIDs from gh_get or gh_document_read.",
                category: "Components",
                parametersSchema: @"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""instanceGuid"": { ""type"": ""string"", ""description"": ""GUID of the target object."" },
                        ""value"": { ""type"": [""string"", ""number"", ""integer"", ""boolean""], ""description"": ""Value to apply."" },
                        ""param"": { ""type"": [""string"", ""null""], ""description"": ""Optional component input parameter name, nickname, or zero-based index."" },
                        ""documentId"": { ""type"": [""string"", ""null""], ""description"": ""Optional active-document identity check from gh_document_list. A non-active document is refused."" }
                    },
                    ""required"": [""instanceGuid"", ""value"" ]
                }",
                execute: this.GhSetValueAsync,
                mutatesCanvas: true,
                tags: new[] { "canvas", "components", "mutating", "values" },
                outputSchema: @"{ ""type"": ""object"", ""properties"": { ""updated"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }, ""failed"": { ""type"": ""object"" }, ""protectedGuids"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }, ""rejectedGuids"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } } }",
                annotations: new AIToolAnnotations(destructiveHint: false));
        }

        private async Task<AIReturn> GhSetValueAsync(AIToolCall toolCall)
        {
            var output = new AIReturn { Request = toolCall };
            try
            {
                toolCall.SkipMetricsValidation = true;
                toolCall.CancellationToken.ThrowIfCancellationRequested();
                var info = toolCall.GetToolCall();
                var args = info.GetArgumentsOrEmpty();
                if (!Guid.TryParse(args["instanceGuid"]?.ToString(), out var guid) || guid == Guid.Empty)
                    throw new ArgumentException("A valid 'instanceGuid' is required.");
                var value = args["value"];
                if (value == null || value.Type == JTokenType.Null) throw new ArgumentException("'value' is required.");
                var param = string.IsNullOrWhiteSpace(args["param"]?.ToString()) ? null : args["param"]!.ToString();
                var requestedDocumentId = args["documentId"]?.ToString();

                var token = toolCall.CancellationToken;
                var document = await CanvasAccess.GetCurrentCanvasAsync(token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("No active Grasshopper document.");
                if (!string.IsNullOrWhiteSpace(requestedDocumentId) &&
                    !string.Equals(document.DocumentID.ToString("D"), requestedDocumentId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The requested document is not active. Switch to it explicitly before editing.");

                var prepared = await CanvasAccess.InvokeInDocumentAsync(document, token, () =>
                {
                    if (document.SolutionDepth > 0) throw new InvalidOperationException("Wait for the active solution to finish.");
                    var target = GhValueTools.Find(document, guid)
                        ?? throw new InvalidOperationException($"No object in the active document matches {guid}.");
                    var protectedIds = CanvasProtection.FilterProtectedGuids(new[] { guid }).Protected;
                    var signature = GhValueTools.Signature(document, new[] { guid });
                    var review = CanvasChangeReviewService.CreateComponentStateSession(
                        info.Name ?? this.toolName,
                        protectedIds.Count == 0 ? new[] { guid } : Array.Empty<Guid>(),
                        CanvasChangeReviewService.DescribeParameterChange(guid, args, "Set value"));
                    review.DocumentId = document.DocumentID;
                    return Task.FromResult((Target: target, Signature: signature, Protected: protectedIds.ToArray(), Review: review));
                }).ConfigureAwait(false);

                if (prepared.Review.Items.Count > 0)
                {
                    await CanvasChangeReviewService.ReviewAsync(prepared.Review, toolCall.InvocationContext, token).ConfigureAwait(false);
                }

                var accepted = CanvasChangeReviewService.GetAcceptedComponentGuids(prepared.Review);
                var payload = await CanvasAccess.InvokeInDocumentAsync(document, token, () =>
                {
                    if (document.SolutionDepth > 0) throw new InvalidOperationException("A solution started during review; retry the value edit.");
                    if (prepared.Protected.Length > 0) return Task.FromResult(new JObject
                    {
                        ["updated"] = new JArray(),
                        ["protectedGuids"] = new JArray(prepared.Protected.Select(id => id.ToString("D"))),
                        ["rejectedGuids"] = new JArray(guid.ToString("D")),
                    });
                    if (!accepted.Contains(guid)) return Task.FromResult(new JObject
                    {
                        ["updated"] = new JArray(),
                        ["protectedGuids"] = new JArray(),
                        ["rejectedGuids"] = new JArray(guid.ToString("D")),
                    });

                    var target = GhValueTools.Find(document, guid);
                    if (!ReferenceEquals(target, prepared.Target) || GhValueTools.Signature(document, new[] { guid }) != prepared.Signature)
                        throw new InvalidOperationException("The reviewed target changed. Retry the value edit.");
                    if (CanvasProtection.IsProtected(guid))
                        throw new InvalidOperationException("The target became protected during review. Retry with an unprotected object.");

                    using var undo = new CanvasUndoBatch(document);
                    var (result, detail) = GhValueTools.SetValue(document, guid, value, param);
                    if (prepared.Signature == GhValueTools.Signature(document, new[] { guid })) undo.DiscardRestoredChanges();
                    var payload = new JObject
                    {
                        ["updated"] = result == GhValueTools.ValueResult.Success ? new JArray(guid.ToString("D")) : new JArray(),
                        ["protectedGuids"] = new JArray(),
                        ["rejectedGuids"] = new JArray(),
                    };
                    if (result != GhValueTools.ValueResult.Success) payload["failed"] = new JObject { [guid.ToString("D")] = detail };
                    return Task.FromResult(payload);
                }).ConfigureAwait(false);

                payload.WithEnvelope(ToolResultEnvelope.Create(info.Name ?? this.toolName, ToolResultContentType.Object));
                output.CreateSuccess(
                    SmartHopper.Infrastructure.AICall.Core.Interactions.AIBodyBuilder.Create()
                        .AddToolResult(payload, id: info.Id, name: info.Name ?? this.toolName)
                        .Build(),
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
