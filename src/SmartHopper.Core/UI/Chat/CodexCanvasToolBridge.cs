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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Newtonsoft.Json.Linq;
using Rhino;
using SmartHopper.Infrastructure.AITools;
using SmartHopper.Infrastructure.AITools.External;
using SmartHopper.Infrastructure.Authentication.Codex;

namespace SmartHopper.Core.UI.Chat
{
    /// <summary>
    /// Codex 도구 호출을 대화를 열 때 캡처한 Grasshopper 문서에만 적용합니다.
    /// </summary>
    internal sealed class CodexCanvasToolBridge : ICodexCanvasToolBridge
    {
        private static readonly HashSet<string> ReadOnlyTools = new (StringComparer.OrdinalIgnoreCase)
        {
            "gh_get", "gh_get_selected", "gh_get_selected_with_data", "gh_get_by_guid",
            "gh_get_by_guid_with_data", "gh_get_errors", "gh_get_errors_with_data", "gh_get_locked",
            "gh_get_preview_off", "gh_get_preview_on", "gh_get_visible", "gh_get_start",
            "gh_get_start_with_data", "gh_get_end", "gh_get_end_with_data", "gh_diff",
            "gh_list_categories", "gh_list_components", "gh_patch_validate", "smarthopper_readme",
            "smarthopper_workflows", "smarthopper_tool_help", "script_review",
        };

        private static readonly HashSet<string> MutationTools = new (StringComparer.OrdinalIgnoreCase)
        {
            "gh_put", "gh_move", "gh_connect", "gh_disconnect", "gh_group", "gh_group_selected",
            "gh_tidy_up", "gh_tidy_up_selected", "gh_component_toggle_lock",
            "gh_component_lock_selected", "gh_component_unlock_selected", "gh_component_toggle_preview",
            "gh_component_hide_preview_selected", "gh_component_show_preview_selected",
            "gh_parameter_data_mapping_flatten", "gh_parameter_data_mapping_graft", "gh_parameter_reverse",
            "gh_parameter_simplify", "script_edit", "script_edit_and_replace_on_canvas",
            "script_parameter_add_input", "script_parameter_add_output", "script_parameter_remove_input",
            "script_parameter_remove_output", "script_parameter_set_type_input", "script_parameter_set_type_output",
            "script_parameter_set_access", "script_toggle_std_output", "script_set_principal_input",
            "script_parameter_set_optional",
        };

        private readonly GH_Document document;
        private readonly Guid documentId;
        private readonly AIToolExternalCatalog catalog;
        private readonly AIToolExternalExecutor executor;
        private readonly CodexToolExecutionState execution = new ();

        public CodexCanvasToolBridge(GH_Document document)
        {
            this.document = document ?? throw new ArgumentNullException(nameof(document));
            this.catalog = new AIToolExternalCatalog();
            this.executor = new AIToolExternalExecutor(this.catalog);
            this.documentId = document.DocumentID;
            this.DocumentId = this.documentId;
            this.CanvasPath = document.FilePath ?? string.Empty;
            this.SessionCreatedAtUtc = DateTime.UtcNow;
            this.CanvasName = string.IsNullOrWhiteSpace(document.FilePath)
                ? $"제목 없는 Canvas ({this.documentId.ToString("N").Substring(0, 8)})"
                : Path.GetFileName(document.FilePath);
            this.RefreshStatus();
        }

        public string CanvasName { get; }

        public Guid DocumentId { get; }

        public string CanvasPath { get; }

        public DateTime SessionCreatedAtUtc { get; }

        public CodexCanvasEditingMode EditingMode { get; set; } = CodexCanvasEditingMode.ReadOnly;

        public int AvailableToolCount { get; private set; }

        public string StatusMessage { get; private set; } = string.Empty;

        public Func<CodexCanvasToolApprovalRequest, CancellationToken, Task<bool>>? ApprovalHandler { get; set; }

        public JArray BuildDynamicTools()
        {
            this.ThrowIfDisposed();
            // thread 생성 뒤에도 사용자가 편집을 켤 수 있도록 안전한 mutation 목록은 descriptor에 유지하고,
            // 실제 실행 시점에 현재 mode와 승인을 다시 검사합니다.
            var options = this.CreateDescriptorOptions();
            var descriptors = this.catalog.BuildDescriptors(options);
            this.AvailableToolCount = descriptors.Count;
            this.RefreshStatus();
            return CodexDynamicToolBridge.BuildNamespace(descriptors);
        }

        public async Task<CodexDynamicToolResult> ExecuteAsync(CodexDynamicToolCall call, CancellationToken cancellationToken)
        {
            this.ThrowIfDisposed();
            if (!string.Equals(call.Namespace, CodexDynamicToolBridge.NamespaceName, StringComparison.Ordinal))
            {
                return CodexDynamicToolResult.Error("지원하지 않는 Canvas 도구 namespace입니다.");
            }

            if (!this.catalog.TryGetTool(call.Tool, out var tool) || !this.catalog.IsExposed(call.Tool, this.CreateOptions()))
            {
                return CodexDynamicToolResult.Error("현재 Canvas session에서 사용할 수 없는 도구입니다.");
            }

            var risk = this.GetRisk(tool);
            if (risk == CodexCanvasToolRisk.Destructive)
            {
                return CodexDynamicToolResult.Error("이 도구는 현재 Canvas Assistant 보안 정책에서 비활성화되어 있습니다.");
            }

            if (risk == CodexCanvasToolRisk.Mutation && this.EditingMode == CodexCanvasEditingMode.ReadOnly)
            {
                return CodexDynamicToolResult.Error("Canvas editing이 비활성화되어 있습니다. 설정 패널에서 변경 전 확인 모드를 켜세요.");
            }

            using var linked = this.execution.CreateLinkedCancellation(cancellationToken);
            var entered = false;
            try
            {
                await this.execution.WaitAsync(linked.Token).ConfigureAwait(false);
                entered = true;
                linked.Token.ThrowIfCancellationRequested();
                var validation = await this.ValidateDocumentAsync(call.Tool, call.Arguments, linked.Token).ConfigureAwait(false);
                if (validation != null)
                {
                    return CodexDynamicToolResult.Error(validation);
                }

                // Freeze selection-based arguments for the approval and its subsequent execution.
                var arguments = (JObject)call.Arguments.DeepClone();
                var selectedBeforeApproval = await this.OnUiThreadAsync(
                    () => this.GetSelectedGuids(call.Tool), linked.Token).ConfigureAwait(false);
                if (selectedBeforeApproval != null)
                {
                    arguments["guids"] = new JArray(selectedBeforeApproval.Select(id => id.ToString("D")));
                }

                if (risk == CodexCanvasToolRisk.Mutation && this.EditingMode == CodexCanvasEditingMode.ConfirmChanges)
                {
                    var approval = this.ApprovalHandler;
                    var approvalRequest = this.CreateApprovalRequest(call, selectedBeforeApproval);
                    if (approval == null || !await approval(approvalRequest, linked.Token).ConfigureAwait(false))
                    {
                        return CodexDynamicToolResult.Error("사용자가 Canvas 변경을 취소했습니다.");
                    }
                }

                JObject before = new ();
                string? dispatchError = null;
                var result = await this.OnUiThreadAsync(
                    () =>
                    {
                        // Recheck after approval, in the same UI callback that starts the tool.
                        linked.Token.ThrowIfCancellationRequested();
                        var error = this.ValidateDocument(call.Tool, arguments);
                        if (error != null)
                        {
                            dispatchError = error;
                            return Task.FromResult(AIToolExternalResult.Error(error));
                        }

                        if (selectedBeforeApproval != null &&
                            !selectedBeforeApproval.ToHashSet().SetEquals(this.GetSelectedGuids(call.Tool) ?? Array.Empty<Guid>()))
                        {
                            dispatchError = "Canvas selection changed while approval was pending. Submit the request again.";
                            return Task.FromResult(AIToolExternalResult.Error(dispatchError));
                        }

                        if (!this.catalog.IsExposed(call.Tool, this.CreateOptions()))
                        {
                            dispatchError = "Canvas editing permissions changed while approval was pending.";
                            return Task.FromResult(AIToolExternalResult.Error(dispatchError));
                        }

                        if (risk == CodexCanvasToolRisk.Mutation)
                        {
                            before = this.CaptureCanvasState(call.Tool, arguments);
                        }

                        return this.executor.ExecuteAsync(
                        call.Tool,
                        arguments,
                        this.CreateOptions(),
                        call.CallId,
                        call.TurnId,
                        linked.Token);
                    },
                    linked.Token).ConfigureAwait(false);

                if (result.IsError)
                {
                    return CodexDynamicToolResult.Error(dispatchError ??
                        $"Canvas 도구 '{call.Tool}' 실행에 실패했습니다. Rhino runtime message를 확인하세요.");
                }

                if (risk == CodexCanvasToolRisk.Mutation)
                {
                    // 실행이 성공한 뒤 들어온 Stop은 이미 적용된 변경을 취소로 오인하지 않도록
                    // postcondition 확인까지 완료한 후 결과를 반환합니다.
                    var after = await this.OnUiThreadAsync(
                        () => this.CaptureCanvasState(call.Tool, arguments, result.Payload),
                        CancellationToken.None).ConfigureAwait(false);
                    var verification = CanvasMutationVerifier.Verify(call.Tool, arguments, result.Payload, before, after);
                    result.Payload["verification"] = new JObject
                    {
                        ["status"] = verification.Status,
                        ["message"] = verification.Message,
                        ["documentId"] = this.documentId.ToString("D"),
                    };
                    if (!verification.IsValid)
                    {
                        return CodexDynamicToolResult.Error(verification.Message);
                    }
                }

                return CodexDynamicToolResult.Ok(result.Payload);
            }
            catch (OperationCanceledException)
            {
                return CodexDynamicToolResult.Error("Canvas 도구 실행이 취소되었습니다.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CodexCanvasToolBridge] Execution or verification failed: {ex}");
                return CodexDynamicToolResult.Error("Canvas tool execution or verification failed. Inspect the Rhino runtime messages before retrying.");
            }
            finally
            {
                if (entered)
                {
                    this.execution.Release();
                }
            }
        }

        public void CancelPending()
        {
            this.execution.CancelPending();
        }

        public void Dispose()
        {
            this.execution.Dispose();
        }

        private AIToolExternalCatalogOptions CreateOptions()
        {
            var names = this.EditingMode == CodexCanvasEditingMode.ReadOnly
                ? ReadOnlyTools
                : new HashSet<string>(ReadOnlyTools.Concat(MutationTools), StringComparer.OrdinalIgnoreCase);
            return new AIToolExternalCatalogOptions
            {
                EnabledTools = names,
                ExposeMutatingTools = this.EditingMode != CodexCanvasEditingMode.ReadOnly,
                AdditionalFilter = tool => ReadOnlyTools.Contains(tool.Name) || MutationTools.Contains(tool.Name),
            };
        }

        private AIToolExternalCatalogOptions CreateDescriptorOptions()
        {
            return new AIToolExternalCatalogOptions
            {
                EnabledTools = new HashSet<string>(ReadOnlyTools.Concat(MutationTools), StringComparer.OrdinalIgnoreCase),
                ExposeMutatingTools = true,
                AdditionalFilter = tool => ReadOnlyTools.Contains(tool.Name) || MutationTools.Contains(tool.Name),
            };
        }

        private CodexCanvasToolRisk GetRisk(AITool tool)
        {
            if (!tool.MutatesCanvas && (ReadOnlyTools.Contains(tool.Name) || MutationTools.Contains(tool.Name)))
            {
                return CodexCanvasToolRisk.ReadOnly;
            }

            return MutationTools.Contains(tool.Name)
                ? CodexCanvasToolRisk.Mutation
                : CodexCanvasToolRisk.Destructive;
        }

        private async Task<string?> ValidateDocumentAsync(
            string toolName,
            JObject arguments,
            CancellationToken cancellationToken)
        {
            return await this.OnUiThreadAsync(() => this.ValidateDocument(toolName, arguments), cancellationToken).ConfigureAwait(false);
        }

        private string? ValidateDocument(string toolName, JObject arguments)
        {
            var active = Instances.ActiveCanvas?.Document;
            var isOpen = Instances.DocumentServer.Any(item => ReferenceEquals(item, this.document));
            if (!isOpen || this.document.DocumentID != this.documentId || !ReferenceEquals(active, this.document))
            {
                return "Canvas Assistant가 연결된 문서와 현재 활성 문서가 다릅니다. 원래 문서로 돌아가거나 새 대화를 시작하세요.";
            }

            var objectIds = this.document.Objects.Select(item => item.InstanceGuid).ToHashSet();
            foreach (var guid in FindTargetGuids(toolName, arguments))
            {
                if (!objectIds.Contains(guid))
                {
                    return $"대상 instance GUID가 연결된 Canvas에 없습니다: {guid:D}";
                }
            }

            return null;
        }

        private CodexCanvasToolApprovalRequest CreateApprovalRequest(CodexDynamicToolCall call, Guid[]? selectedGuids = null)
        {
            var guids = (selectedGuids ?? FindTargetGuids(call.Tool, call.Arguments)).Distinct().ToList();
            return new CodexCanvasToolApprovalRequest
            {
                ToolName = call.Tool,
                Summary = guids.Count == 0
                    ? $"{call.Tool} 도구가 연결된 Canvas를 변경하려고 합니다."
                    : $"{call.Tool} 도구가 {guids.Count}개 Canvas 객체를 변경하려고 합니다.",
                AffectedGuidCount = guids.Count,
                Risk = CodexCanvasToolRisk.Mutation,
            };
        }

        private Guid[]? GetSelectedGuids(string toolName)
        {
            return toolName.EndsWith("_selected", StringComparison.OrdinalIgnoreCase)
                ? this.document.Objects.Where(item => item.Attributes?.Selected == true).Select(item => item.InstanceGuid).ToArray()
                : null;
        }

        private JObject CaptureCanvasState(string toolName, JObject arguments, JObject? payload = null)
        {
            var state = new JObject();
            var name = toolName.ToLowerInvariant();
            var targets = FindTargetGuids(toolName, arguments).ToHashSet();
            var createsObjects = name == "gh_put" || name == "gh_group" || name == "gh_group_selected";
            if (createsObjects && payload == null)
            {
                // Creation needs a complete existence baseline, not every object's fields.
                foreach (var item in this.document.Objects)
                {
                    state[item.InstanceGuid.ToString("D")] = new JObject();
                }

                return state;
            }

            if (createsObjects)
            {
                var reported = name == "gh_put"
                    ? payload?["instanceGuids"] as JArray ?? new JArray()
                    : new JArray(payload?["group"] ?? payload?["groupGuid"]);
                targets = reported.Select(token => Guid.TryParse(token?.ToString(), out var id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty).ToHashSet();
            }

            var capturePosition = name == "gh_move" || name == "gh_tidy_up" || name == "gh_tidy_up_selected";
            var captureLock = name.Contains("toggle_lock") || name.Contains("lock_selected") || name.Contains("unlock_selected");
            var capturePreview = name.Contains("toggle_preview") || name.Contains("preview_selected");
            var captureConnections = name == "gh_connect" || name == "gh_disconnect";
            var captureSerialized = toolName.StartsWith("script_", StringComparison.OrdinalIgnoreCase) ||
                toolName.StartsWith("gh_parameter_", StringComparison.OrdinalIgnoreCase);
            foreach (var id in targets)
            {
                var item = this.document.FindObject(id, false);
                if (item == null) continue;
                var component = item as IGH_Component;
                var parameter = item as IGH_Param;
                var value = new JObject();
                if (capturePosition)
                {
                    value["x"] = item.Attributes?.Pivot.X;
                    value["y"] = item.Attributes?.Pivot.Y;
                }

                if (captureLock) value["locked"] = component != null ? (bool?)component.Locked : parameter?.Locked;
                if (capturePreview) value["hidden"] = item is IGH_PreviewObject preview ? (bool?)preview.Hidden : null;
                if ((captureLock || capturePreview) && name.EndsWith("_selected", StringComparison.Ordinal))
                {
                    value["selected"] = item.Attributes?.Selected == true;
                }

                if (captureConnections)
                {
                    var standalone = parameter != null ? new[] { parameter } : Array.Empty<IGH_Param>();
                    value["inputs"] = new JArray((component != null ? component.Params.Input : (IEnumerable<IGH_Param>)standalone)
                        .Select(port => CaptureParameter(port, true)));
                    value["outputs"] = new JArray((component != null ? component.Params.Output : (IEnumerable<IGH_Param>)standalone)
                        .Select(port => CaptureParameter(port, false)));
                }

                if (createsObjects && item is GH_Group group)
                {
                    value["members"] = new JArray(group.ObjectIDs.Select(id => id.ToString("D")));
                }

                if (captureSerialized)
                {
                    var chunk = new GH_LooseChunk("CanvasVerification");
                    if (!item.Write(chunk))
                    {
                        throw new InvalidOperationException("The target object could not be serialized for Canvas verification.");
                    }

                    value["serialized"] = Convert.ToBase64String(chunk.Serialize_Binary());
                }

                state[item.InstanceGuid.ToString("D")] = value;
            }

            return state;
        }

        private static JObject CaptureParameter(IGH_Param parameter, bool captureSources)
        {
            var state = new JObject
            {
                ["guid"] = parameter.InstanceGuid.ToString("D"),
                ["name"] = parameter.Name,
                ["nickname"] = parameter.NickName,
            };
            if (captureSources) state["sources"] = new JArray(parameter.Sources.Select(source => source.InstanceGuid.ToString("D")));
            return state;
        }

        private static IEnumerable<Guid> FindTargetGuids(string toolName, JToken token)
        {
            if (string.Equals(toolName, "gh_put", StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<Guid>();
            }

            return FindArgumentGuids(token, false).Distinct().ToArray();
        }

        private static IEnumerable<Guid> FindArgumentGuids(JToken token, bool guidContext)
        {
            if (token is JProperty property)
            {
                if (Guid.TryParse(property.Name, out var keyGuid))
                {
                    yield return keyGuid;
                }

                var propertyIsGuid = guidContext || property.Name.IndexOf("guid", StringComparison.OrdinalIgnoreCase) >= 0;
                foreach (var propertyGuid in FindArgumentGuids(property.Value, propertyIsGuid))
                {
                    yield return propertyGuid;
                }
                yield break;
            }

            if (guidContext && token is JValue value && Guid.TryParse(value.Value<string>(), out var valueGuid))
            {
                yield return valueGuid;
            }

            foreach (var child in token.Children())
            {
                foreach (var childGuid in FindArgumentGuids(child, guidContext))
                {
                    yield return childGuid;
                }
            }
        }

        private Task<T> OnUiThreadAsync<T>(Func<T> action, CancellationToken cancellationToken)
        {
            return this.OnUiThreadAsync<T>(() => Task.FromResult(action()), cancellationToken);
        }

        private async Task<T> OnUiThreadAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Only cancel queued callbacks. Once dispatched, retain the execution gate until the
            // callback finishes so a second mutation cannot overlap an operation still running.
            var dispatchState = 0;
            using var registration = cancellationToken.Register(() =>
            {
                if (Interlocked.CompareExchange(ref dispatchState, 2, 0) == 0)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
            });
            RhinoApp.InvokeOnUiThread(async () =>
            {
                if (Interlocked.CompareExchange(ref dispatchState, 1, 0) != 0)
                {
                    return;
                }

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    completion.TrySetResult(await action().ConfigureAwait(true));
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
            return await completion.Task.ConfigureAwait(false);
        }

        private void RefreshStatus()
        {
            this.StatusMessage = this.EditingMode == CodexCanvasEditingMode.ReadOnly
                ? $"읽기 전용 · {this.CanvasName} · 도구 {this.AvailableToolCount}개"
                : $"변경 전 확인 · {this.CanvasName} · 도구 {this.AvailableToolCount}개";
        }

        private void ThrowIfDisposed()
        {
            this.execution.ThrowIfDisposed(nameof(CodexCanvasToolBridge));
        }
    }
}
