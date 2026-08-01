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
using Grasshopper;
using Grasshopper.Kernel;
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
        private readonly SemaphoreSlim executionGate = new (1, 1);
        private readonly object cancellationSync = new ();
        private CancellationTokenSource pendingCancellation = new ();
        private int disposed;

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
            if (descriptors.Count == 0)
            {
                return new JArray();
            }

            return new JArray
            {
                new JObject
                {
                    ["type"] = "namespace",
                    ["name"] = CodexDynamicToolBridge.NamespaceName,
                    ["description"] = "현재 연결된 Grasshopper Canvas를 조회하고 안전하게 수정하는 SmartHopper 도구",
                    ["tools"] = new JArray(descriptors.Select(item => new JObject
                    {
                        ["type"] = "function",
                        ["name"] = item.Name,
                        ["description"] = item.Description,
                        ["inputSchema"] = item.InputSchema.DeepClone(),
                    })),
                },
            };
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

            using var linked = this.CreateLinkedCancellation(cancellationToken);
            await this.executionGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                var validation = await this.ValidateDocumentAsync(call.Tool, call.Arguments, linked.Token).ConfigureAwait(false);
                if (validation != null)
                {
                    return CodexDynamicToolResult.Error(validation);
                }

                if (risk == CodexCanvasToolRisk.Mutation && this.EditingMode == CodexCanvasEditingMode.ConfirmChanges)
                {
                    var approval = this.ApprovalHandler;
                    if (approval == null || !await approval(this.CreateApprovalRequest(call), linked.Token).ConfigureAwait(false))
                    {
                        return CodexDynamicToolResult.Error("사용자가 Canvas 변경을 취소했습니다.");
                    }
                }

                var before = risk == CodexCanvasToolRisk.Mutation
                    ? await this.OnUiThreadAsync(this.CreateDocumentFingerprint, linked.Token).ConfigureAwait(false)
                    : string.Empty;
                var result = await this.OnUiThreadAsync(
                    () => this.executor.ExecuteAsync(
                        call.Tool,
                        call.Arguments,
                        this.CreateOptions(),
                        call.CallId,
                        call.TurnId,
                        linked.Token),
                    linked.Token).ConfigureAwait(false);

                if (result.IsError)
                {
                    return CodexDynamicToolResult.Error(
                        $"Canvas 도구 '{call.Tool}' 실행에 실패했습니다. Rhino runtime message를 확인하세요.");
                }

                if (risk == CodexCanvasToolRisk.Mutation)
                {
                    // 실행이 성공한 뒤 들어온 Stop은 이미 적용된 변경을 취소로 오인하지 않도록
                    // postcondition 확인까지 완료한 후 결과를 반환합니다.
                    var after = await this.OnUiThreadAsync(
                        this.CreateDocumentFingerprint,
                        CancellationToken.None).ConfigureAwait(false);
                    result.Payload["verification"] = new JObject
                    {
                        ["status"] = string.Equals(before, after, StringComparison.Ordinal)
                            ? "VerificationFailed"
                            : "Succeeded",
                        ["documentId"] = this.documentId.ToString("D"),
                    };
                    if (string.Equals(before, after, StringComparison.Ordinal))
                    {
                        return CodexDynamicToolResult.Error("도구는 완료되었지만 Canvas 변경을 확인하지 못했습니다.");
                    }
                }

                return CodexDynamicToolResult.Ok(result.Payload);
            }
            catch (OperationCanceledException)
            {
                return CodexDynamicToolResult.Error("Canvas 도구 실행이 취소되었습니다.");
            }
            finally
            {
                this.executionGate.Release();
            }
        }

        public void CancelPending()
        {
            CancellationTokenSource? cancelled = null;
            lock (this.cancellationSync)
            {
                if (Volatile.Read(ref this.disposed) == 0)
                {
                    cancelled = this.pendingCancellation;
                    this.pendingCancellation = new CancellationTokenSource();
                }
            }

            cancelled?.Cancel();
            cancelled?.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            {
                return;
            }

            lock (this.cancellationSync)
            {
                this.pendingCancellation.Cancel();
                this.pendingCancellation.Dispose();
            }
            // 진행 중인 UI 실행이 finally에서 gate를 반환할 수 있으므로 여기서 dispose하지 않습니다.
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
            if (ReadOnlyTools.Contains(tool.Name) && !tool.MutatesCanvas)
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
            return await this.OnUiThreadAsync(() =>
            {
                var active = Instances.ActiveCanvas?.Document;
                var isOpen = Instances.DocumentServer.Any(item => ReferenceEquals(item, this.document));
                if (!isOpen || this.document.DocumentID != this.documentId || !ReferenceEquals(active, this.document))
                {
                    return "Canvas Assistant가 연결된 문서와 현재 활성 문서가 다릅니다. 원래 문서로 돌아가거나 새 대화를 시작하세요.";
                }

                foreach (var guid in FindTargetGuids(toolName, arguments))
                {
                    if (!this.document.Objects.Any(item => item.InstanceGuid == guid))
                    {
                        return $"대상 instance GUID가 연결된 Canvas에 없습니다: {guid:D}";
                    }
                }

                return null;
            }, cancellationToken).ConfigureAwait(false);
        }

        private CodexCanvasToolApprovalRequest CreateApprovalRequest(CodexDynamicToolCall call)
        {
            var guids = FindTargetGuids(call.Tool, call.Arguments).Distinct().ToList();
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

        private string CreateDocumentFingerprint()
        {
            return string.Join("\n", this.document.Objects
                .OrderBy(item => item.InstanceGuid)
                .Select(item => $"{item.InstanceGuid:D}|{item.NickName}|{item.Attributes?.Pivot.X:R},{item.Attributes?.Pivot.Y:R}|{CreateWireFingerprint(item)}"));
        }

        private static string CreateWireFingerprint(IGH_DocumentObject item)
        {
            if (item is not GH_Component component)
            {
                return string.Empty;
            }

            return string.Join(",", component.Params.Input
                .SelectMany(parameter => parameter.Sources)
                .Select(source => source.InstanceGuid)
                .OrderBy(id => id));
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

        private CancellationTokenSource CreateLinkedCancellation(CancellationToken cancellationToken)
        {
            lock (this.cancellationSync)
            {
                return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.pendingCancellation.Token);
            }
        }

        private Task<T> OnUiThreadAsync<T>(Func<T> action, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            RhinoApp.InvokeOnUiThread(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    completion.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
            return completion.Task;
        }

        private Task<T> OnUiThreadAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            RhinoApp.InvokeOnUiThread(async () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    completion.TrySetResult(await action().ConfigureAwait(true));
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
            return completion.Task;
        }

        private void RefreshStatus()
        {
            this.StatusMessage = this.EditingMode == CodexCanvasEditingMode.ReadOnly
                ? $"읽기 전용 · {this.CanvasName} · 도구 {this.AvailableToolCount}개"
                : $"변경 전 확인 · {this.CanvasName} · 도구 {this.AvailableToolCount}개";
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref this.disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(CodexCanvasToolBridge));
            }
        }
    }
}
