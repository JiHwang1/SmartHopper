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
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SmartHopper.Infrastructure.AITools.External;

namespace SmartHopper.Infrastructure.Authentication.Codex
{
    public enum CodexCanvasEditingMode
    {
        ReadOnly,
        ConfirmChanges,
        TrustedSession,
    }

    public enum CodexCanvasToolRisk
    {
        ReadOnly,
        Mutation,
        Destructive,
    }

    public sealed class CodexCanvasToolApprovalRequest
    {
        public string ToolName { get; init; } = string.Empty;

        public string Summary { get; init; } = string.Empty;

        public int AffectedGuidCount { get; init; }

        public CodexCanvasToolRisk Risk { get; init; }
    }

    public interface ICodexCanvasToolBridge : ICodexDynamicToolBridge
    {
        string CanvasName { get; }

        Guid DocumentId { get; }

        string CanvasPath { get; }

        DateTime SessionCreatedAtUtc { get; }

        CodexCanvasEditingMode EditingMode { get; set; }

        int AvailableToolCount { get; }

        string StatusMessage { get; }

        Func<CodexCanvasToolApprovalRequest, CancellationToken, Task<bool>>? ApprovalHandler { get; set; }
    }

    public interface ICodexCanvasToolSession
    {
        ICodexCanvasToolBridge? CanvasTools { get; }
    }

    /// <summary>
    /// Codex Dynamic Tools가 전달한 검증된 SmartHopper 호출입니다.
    /// </summary>
    public sealed class CodexDynamicToolCall
    {
        public string ThreadId { get; init; } = string.Empty;

        public string TurnId { get; init; } = string.Empty;

        public string CallId { get; init; } = string.Empty;

        public string Namespace { get; init; } = string.Empty;

        public string Tool { get; init; } = string.Empty;

        public JObject Arguments { get; init; } = new ();

        public static bool TryParse(JObject parameters, out CodexDynamicToolCall? call)
        {
            call = null;
            if (parameters == null ||
                parameters["arguments"] is not JObject arguments)
            {
                return false;
            }

            var threadId = parameters.Value<string>("threadId");
            var turnId = parameters.Value<string>("turnId");
            var callId = parameters.Value<string>("callId");
            var toolNamespace = parameters.Value<string>("namespace");
            var tool = parameters.Value<string>("tool");
            if (string.IsNullOrWhiteSpace(threadId) ||
                string.IsNullOrWhiteSpace(turnId) ||
                string.IsNullOrWhiteSpace(callId) ||
                string.IsNullOrWhiteSpace(toolNamespace) ||
                string.IsNullOrWhiteSpace(tool))
            {
                return false;
            }

            call = new CodexDynamicToolCall
            {
                ThreadId = threadId,
                TurnId = turnId,
                CallId = callId,
                Namespace = toolNamespace,
                Tool = tool,
                Arguments = (JObject)arguments.DeepClone(),
            };
            return true;
        }
    }

    /// <summary>
    /// Codex app-server에 돌려줄 Dynamic Tool 결과입니다.
    /// </summary>
    public sealed class CodexDynamicToolResult
    {
        public System.Collections.Generic.IReadOnlyList<SmartHopper.Infrastructure.AICall.Core.Interactions.ToolResultImage> Images { get; init; }
            = System.Array.Empty<SmartHopper.Infrastructure.AICall.Core.Interactions.ToolResultImage>();
        public bool Success { get; init; }

        public JObject Payload { get; init; } = new ();

        public string? ErrorMessage { get; init; }

        public JObject ToResponse()
        {
            var content = this.Success
                ? this.Payload
                : new JObject
                {
                    ["error"] = this.ErrorMessage ?? "Canvas 도구 실행에 실패했습니다.",
                };
            var response = new JObject
            {
                ["success"] = this.Success,
                ["contentItems"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "inputText",
                        ["text"] = content.ToString(Formatting.None),
                    },
                },
            };
            if (this.Success)
            {
                foreach (var image in this.Images)
                {
                    if (image.SendToModel && SmartHopper.Infrastructure.AICall.Utilities.ToolResultMediaExtractor.IsSafeImage(image))
                        ((JArray)response["contentItems"]).Add(new JObject { ["type"] = "inputImage", ["imageUrl"] = $"data:{image.MimeType};base64,{image.ImageData}" });
                }
            }
            return response;
        }

        public static CodexDynamicToolResult Ok(JObject payload, System.Collections.Generic.IReadOnlyList<SmartHopper.Infrastructure.AICall.Core.Interactions.ToolResultImage>? images = null)
        {
            return new CodexDynamicToolResult
            {
                Success = true,
                Payload = payload != null ? (JObject)payload.DeepClone() : new JObject(),
                Images = images ?? System.Array.Empty<SmartHopper.Infrastructure.AICall.Core.Interactions.ToolResultImage>(),
            };
        }

        public static CodexDynamicToolResult Error(string message)
        {
            return new CodexDynamicToolResult { ErrorMessage = message };
        }
    }

    /// <summary>
    /// 대화 session과 Canvas 실행 정책 사이의 Dynamic Tools 경계입니다.
    /// </summary>
    public interface ICodexDynamicToolBridge : IDisposable
    {
        JArray BuildDynamicTools();

        Task<CodexDynamicToolResult> ExecuteAsync(CodexDynamicToolCall call, CancellationToken cancellationToken);

        void CancelPending();
    }

    /// <summary>Shared execution ownership and cancellation for Dynamic Tool bridges.</summary>
    internal sealed class CodexToolExecutionState : IDisposable
    {
        private readonly SemaphoreSlim gate = new (1, 1);
        private readonly object sync = new ();
        private CancellationTokenSource pending = new ();
        private bool disposed;

        internal void ThrowIfDisposed(string owner)
        {
            lock (this.sync)
            {
                if (this.disposed) throw new ObjectDisposedException(owner);
            }
        }

        internal CancellationTokenSource CreateLinkedCancellation(CancellationToken cancellationToken)
        {
            lock (this.sync)
            {
                if (this.disposed) throw new ObjectDisposedException(nameof(CodexToolExecutionState));
                return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.pending.Token);
            }
        }

        internal Task WaitAsync(CancellationToken cancellationToken) => this.gate.WaitAsync(cancellationToken);

        internal void Release() => this.gate.Release();

        internal void CancelPending()
        {
            CancellationTokenSource cancelled;
            lock (this.sync)
            {
                if (this.disposed) return;
                cancelled = this.pending;
                this.pending = new CancellationTokenSource();
            }

            cancelled.Cancel();
            cancelled.Dispose();
        }

        public void Dispose()
        {
            CancellationTokenSource cancelled;
            lock (this.sync)
            {
                if (this.disposed) return;
                this.disposed = true;
                cancelled = this.pending;
            }

            cancelled.Cancel();
            cancelled.Dispose();
            // Keep the gate alive until an already dispatched mutation releases ownership.
        }
    }

    /// <summary>
    /// 공통 AITool adapter를 Codex namespace descriptor와 실행 결과로 변환합니다.
    /// Canvas 문서 확인과 승인 정책은 추가 필터 및 실행 callback에서 주입합니다.
    /// </summary>
    public sealed class CodexDynamicToolBridge : ICodexDynamicToolBridge
    {
        public const string NamespaceName = "smarthopper";

        private readonly AIToolExternalCatalog catalog;
        private readonly AIToolExternalExecutor executor;
        private readonly AIToolExternalCatalogOptions options;
        private readonly CodexToolExecutionState execution = new ();

        public CodexDynamicToolBridge(
            AIToolExternalCatalog catalog,
            AIToolExternalExecutor executor,
            AIToolExternalCatalogOptions options)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
            this.options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public JArray BuildDynamicTools()
        {
            this.ThrowIfDisposed();
            var tools = this.catalog.BuildDescriptors(this.options);
            return BuildNamespace(tools);
        }

        internal static JArray BuildNamespace(IReadOnlyList<AIToolExternalDescriptor> tools)
        {
            if (tools.Count == 0)
            {
                return new JArray();
            }

            return new JArray
            {
                new JObject
                {
                    ["type"] = "namespace",
                    ["name"] = NamespaceName,
                    ["description"] = "현재 연결된 Grasshopper Canvas를 조회하고 안전하게 수정하는 SmartHopper 도구",
                    ["tools"] = new JArray(tools.Select(tool => new JObject
                    {
                        ["type"] = "function",
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["inputSchema"] = tool.InputSchema.DeepClone(),
                    })),
                },
            };
        }

        public async Task<CodexDynamicToolResult> ExecuteAsync(
            CodexDynamicToolCall call,
            CancellationToken cancellationToken)
        {
            this.ThrowIfDisposed();
            if (call == null)
            {
                throw new ArgumentNullException(nameof(call));
            }

            if (!string.Equals(call.Namespace, NamespaceName, StringComparison.Ordinal))
            {
                return CodexDynamicToolResult.Error("지원하지 않는 Canvas 도구 namespace입니다.");
            }

            using var linkedCancellation = this.execution.CreateLinkedCancellation(cancellationToken);
            await this.execution.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            try
            {
                var result = await this.executor.ExecuteAsync(
                    call.Tool,
                    call.Arguments,
                    this.options,
                    call.CallId,
                    call.TurnId,
                    linkedCancellation.Token).ConfigureAwait(false);
                return result.IsError
                    ? CodexDynamicToolResult.Error(CreateSafeToolError(call.Tool))
                    : CodexDynamicToolResult.Ok(result.Payload);
            }
            finally
            {
                this.execution.Release();
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

        private static string CreateSafeToolError(string toolName)
        {
            return $"Canvas 도구 '{toolName}' 실행에 실패했습니다. Rhino runtime message를 확인하세요.";
        }

        private void ThrowIfDisposed()
        {
            this.execution.ThrowIfDisposed(nameof(CodexDynamicToolBridge));
        }
    }
}
