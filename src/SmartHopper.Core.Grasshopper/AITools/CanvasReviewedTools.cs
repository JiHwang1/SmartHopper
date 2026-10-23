using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using GhJSON.Core;
using GhJSON.Grasshopper;
using GhJSON.Grasshopper.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Newtonsoft.Json.Linq;
using SmartHopper.Core.Grasshopper.Utils.Canvas;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.Core.Returns;
using SmartHopper.Infrastructure.AICall.Tools;

namespace SmartHopper.Core.Grasshopper.AITools
{
    /// <summary>Stages structural operations and applies only reviewed items to the captured document.</summary>
    internal static class CanvasReviewedTools
    {
        private static string Signature(GH_Document document, IEnumerable<Guid> ids)
        {
            var requested = ids.ToHashSet();
            return GhJson.ToJson(GhJsonGrasshopper.Serialize(document.Objects.Where(o => requested.Contains(o.InstanceGuid)), new SerializationOptions
            {
                IncludeConnections = true, IncludeGroups = true, IncludeInternalizedData = true,
                IncludeRuntimeData = false, IncludeRuntimeMessages = false, IncludeMetadata = false, IncludeSelectedState = false,
            }));
        }

        private static void CheckReady(GH_Document document)
        {
            if (document.SolutionState == GH_ProcessStep.Process) throw new InvalidOperationException("Wait for the active solution to finish.");
        }

        private static AIReturn Result(AIToolCall call, JObject payload)
        {
            payload.WithEnvelope(ToolResultEnvelope.Create(call.GetToolCall().Name, ToolResultContentType.Object));
            var result = new AIReturn();
            result.CreateSuccess(AIBodyBuilder.Create().AddToolResult(payload, id: call.GetToolCall().Id, name: call.GetToolCall().Name).Build(), call);
            return result;
        }

        internal static async Task<AIReturn> ConnectionsAsync(AIToolCall call, bool disconnect)
        {
            try
            {
                call.SkipMetricsValidation = true;
                var args = call.GetToolCall().GetArgumentsOrEmpty();
                var specs = args["connections"] as JArray ?? throw new ArgumentException("connections is required.");
                if (specs.Count == 0) throw new ArgumentException("connections must not be empty.");
                var proposals = specs.Select(spec => new CanvasConnectionReviewProposal
                {
                    SourceGuid = Guid.Parse(spec["sourceGuid"]?.ToString() ?? string.Empty),
                    TargetGuid = Guid.Parse(spec["targetGuid"]?.ToString() ?? string.Empty),
                    SourceParameter = spec["sourceParam"]?.ToString(), TargetParameter = spec["targetParam"]?.ToString(),
                }).ToList();
                var token = call.CancellationToken;
                var document = await CanvasAccess.GetCurrentCanvasAsync(token).ConfigureAwait(false);
                var ids = proposals.SelectMany(p => new[] { p.SourceGuid, p.TargetGuid }).Distinct().ToArray();
                var prepared = await CanvasAccess.InvokeInDocumentAsync(document, token, () =>
                {
                    CheckReady(document);
                    return Task.FromResult((Signature: Signature(document, ids), Review: CanvasChangeReviewService.CreateConnectionSession(
                        call.GetToolCall().Name, proposals, disconnect ? CanvasChangeKind.ConnectionRemoved : CanvasChangeKind.ConnectionAdded)));
                }).ConfigureAwait(false);
                await CanvasChangeReviewService.ReviewAsync(prepared.Review, call.InvocationContext, token).ConfigureAwait(false);
                var accepted = CanvasChangeReviewService.GetAcceptedConnectionProposalIndexes(prepared.Review);
                var payload = await CanvasAccess.InvokeInDocumentAsync(document, token, () =>
                {
                    CheckReady(document);
                    if (prepared.Signature != Signature(document, ids)) throw new InvalidOperationException("Reviewed objects changed. Retry the proposal.");
                    using var undo = new CanvasUndoBatch(document);
                    var successful = new JArray();
                    var failed = new JArray();
                    for (var index = 0; index < proposals.Count; index++)
                    {
                        var p = proposals[index];
                        var allowed = accepted.Contains(index) && !CanvasProtection.IsProtected(p.SourceGuid) && !CanvasProtection.IsProtected(p.TargetGuid);
                        var applied = allowed && (disconnect
                            ? GhJsonGrasshopper.Disconnect(p.SourceGuid, p.TargetGuid, p.SourceParameter, p.TargetParameter)
                            : GhJsonGrasshopper.Connect(p.SourceGuid, p.TargetGuid, p.SourceParameter, p.TargetParameter));
                        var record = new JObject { ["sourceGuid"] = p.SourceGuid.ToString("D"), ["targetGuid"] = p.TargetGuid.ToString("D"),
                            ["sourceParam"] = p.SourceParameter, ["targetParam"] = p.TargetParameter };
                        if (applied) { record["status"] = disconnect ? "disconnected" : "connected"; successful.Add(record); }
                        else { record["error"] = allowed ? "Connection operation failed." : "Change was rejected, unavailable, or protected."; failed.Add(record); }
                    }
                    if (successful.Count > 0) { document.NewSolution(false); Instances.RedrawCanvas(); }
                    if (prepared.Signature == Signature(document, ids)) undo.DiscardRestoredChanges();
                    return Task.FromResult(new JObject { ["successful"] = successful, ["failed"] = failed, ["successCount"] = successful.Count, ["failCount"] = failed.Count });
                }).ConfigureAwait(false);
                return Result(call, payload);
            }
            catch (Exception ex) { var result = new AIReturn(); result.CreateToolError(ex.Message, call); return result; }
        }

        internal static async Task<AIReturn> RemoveAsync(AIToolCall call)
        {
            try
            {
                call.SkipMetricsValidation = true;
                var ids = (call.GetToolCall().GetArgumentsOrEmpty()["instanceGuids"] as JArray)?.Values<string>().Select(Guid.Parse).Distinct().ToArray()
                    ?? throw new ArgumentException("instanceGuids is required.");
                var token = call.CancellationToken;
                var document = await CanvasAccess.GetCurrentCanvasAsync(token).ConfigureAwait(false);
                var prepared = await CanvasAccess.InvokeInDocumentAsync(document, token, () =>
                {
                    CheckReady(document);
                    return Task.FromResult((Signature: Signature(document, ids), Review: CanvasChangeReviewService.CreateRemovalSession(call.GetToolCall().Name, ids)));
                }).ConfigureAwait(false);
                await CanvasChangeReviewService.ReviewAsync(prepared.Review, call.InvocationContext, token).ConfigureAwait(false);
                var accepted = CanvasChangeReviewService.GetAcceptedRemovalGuids(prepared.Review).ToHashSet();
                var payload = await CanvasAccess.InvokeInDocumentAsync(document, token, () =>
                {
                    CheckReady(document);
                    if (prepared.Signature != Signature(document, ids)) throw new InvalidOperationException("Reviewed objects changed. Retry the removal.");
                    using var undo = new CanvasUndoBatch(document);
                    var protectedIds = ids.Where(CanvasProtection.IsProtected).ToArray();
                    var allowed = ids.Where(id => accepted.Contains(id) && !protectedIds.Contains(id)).ToArray();
                    var removed = CanvasAccess.RemoveInstances(allowed);
                    return Task.FromResult(new JObject { ["removedGuids"] = JArray.FromObject(removed), ["notFoundGuids"] = JArray.FromObject(allowed.Except(removed)),
                        ["protectedGuids"] = JArray.FromObject(protectedIds), ["rejectedGuids"] = JArray.FromObject(ids.Except(accepted)), ["success"] = removed.Count == ids.Length });
                }).ConfigureAwait(false);
                return Result(call, payload);
            }
            catch (Exception ex) { var result = new AIReturn(); result.CreateToolError(ex.Message, call); return result; }
        }
    }
}
