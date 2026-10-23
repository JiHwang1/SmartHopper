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
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SmartHopper.Infrastructure.Utils;

namespace SmartHopper.Infrastructure.Authentication.Codex
{
    /// <summary>
    /// Newline-delimited JSON-RPC client for Codex app-server stdio.
    /// </summary>
    public sealed class CodexJsonRpcClient : IAsyncDisposable
    {
        private readonly TextReader reader;
        private readonly TextWriter writer;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JToken>> pending = new ();
        private readonly ConcurrentDictionary<string, IncomingRequestState> incoming = new (StringComparer.Ordinal);
        private readonly SemaphoreSlim writeGate = new (1, 1);
        private readonly CancellationTokenSource readCancellation = new ();
        private long nextRequestId;
        private Task? readTask;
        private int completed;

        public CodexJsonRpcClient(TextReader reader, TextWriter writer)
        {
            this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
            this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        }

        public event EventHandler<CodexNotificationEventArgs>? NotificationReceived;

        public event EventHandler? MalformedMessageReceived;

        public CodexServerRequestHandler? ServerRequestHandler { get; set; }

        public void Start()
        {
            if (this.readTask != null)
            {
                return;
            }

            this.readTask = this.ReadLoopAsync(this.readCancellation.Token);
        }

        public async Task<JToken> SendRequestAsync(
            string method,
            JObject? parameters = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(method))
            {
                throw new ArgumentException("JSON-RPC method is required.", nameof(method));
            }

            this.ThrowIfCompleted();
            var id = Interlocked.Increment(ref this.nextRequestId);
            var completion = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!this.pending.TryAdd(id, completion))
            {
                throw new InvalidOperationException("Could not register a Codex request.");
            }

            try
            {
                var request = new JObject
                {
                    ["jsonrpc"] = JsonRpcEnvelope.Version,
                    ["id"] = id,
                    ["method"] = method,
                };
                if (parameters != null)
                {
                    request["params"] = parameters;
                }

                await this.WriteAsync(request, cancellationToken).ConfigureAwait(false);
                return await completion.Task
                    .WaitAsync(timeout ?? TimeSpan.FromSeconds(30), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                this.pending.TryRemove(id, out _);
            }
        }

        public Task SendNotificationAsync(string method, JObject? parameters = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(method))
            {
                throw new ArgumentException("JSON-RPC method is required.", nameof(method));
            }

            this.ThrowIfCompleted();
            var notification = new JObject
            {
                ["jsonrpc"] = JsonRpcEnvelope.Version,
                ["method"] = method,
            };
            if (parameters != null)
            {
                notification["params"] = parameters;
            }

            return this.WriteAsync(notification, cancellationToken);
        }

        public Task SendResponseAsync(
            JToken requestId,
            JToken? result,
            CancellationToken cancellationToken = default)
        {
            return this.CompleteIncomingRequestAsync(
                requestId,
                JsonRpcEnvelope.Result(requestId, result),
                cancellationToken);
        }

        public Task SendErrorAsync(
            JToken requestId,
            int code,
            string message,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("JSON-RPC error message is required.", nameof(message));
            }

            return this.CompleteIncomingRequestAsync(
                requestId,
                JsonRpcEnvelope.Error(requestId, code, message),
                cancellationToken);
        }

        /// <summary>
        /// Processes one JSONL message. Public for deterministic transport tests.
        /// </summary>
        public Task ProcessLineAsync(string line, CancellationToken cancellationToken = default)
        {
            return this.ProcessLineAsync(line, cancellationToken, waitForHandler: true);
        }

        private async Task ProcessLineAsync(string line, CancellationToken cancellationToken, bool waitForHandler)
        {
            JObject message;
            try
            {
                message = JObject.Parse(line);
            }
            catch (JsonReaderException)
            {
                this.MalformedMessageReceived?.Invoke(this, EventArgs.Empty);
                return;
            }

            // Codex app-server also emits envelopes without jsonrpc; retain that wire compatibility.
            if (message["jsonrpc"] != null && (message["jsonrpc"]!.Type != JTokenType.String ||
                !string.Equals(message.Value<string>("jsonrpc"), JsonRpcEnvelope.Version, StringComparison.Ordinal)))
            {
                this.MalformedMessageReceived?.Invoke(this, EventArgs.Empty);
                return;
            }

            var requestIdNode = message["id"];
            var methodNode = message["method"];
            var method = methodNode?.Type == JTokenType.String ? methodNode.Value<string>() : null;
            var hasResult = message.TryGetValue("result", out var resultNode);
            var hasError = message.TryGetValue("error", out var errorNode);
            if (requestIdNode != null && methodNode == null && (hasResult || hasError))
            {
                if (hasResult == hasError || !IsValidRequestId(requestIdNode) ||
                    (hasError && (errorNode is not JObject responseError ||
                        responseError["code"]?.Type != JTokenType.Integer ||
                        !int.TryParse(responseError["code"]!.ToString(Formatting.None), out _) ||
                        responseError["message"]?.Type != JTokenType.String)))
                {
                    this.MalformedMessageReceived?.Invoke(this, EventArgs.Empty);
                    return;
                }

                if (requestIdNode.Type == JTokenType.Integer &&
                    long.TryParse(requestIdNode.ToString(Formatting.None), out var responseId) &&
                    this.pending.TryGetValue(responseId, out var completion))
                {
                    if (errorNode is JObject error)
                    {
                        completion.TrySetException(new CodexJsonRpcException(error.Value<int?>("code") ?? -32603));
                    }
                    else
                    {
                        completion.TrySetResult(resultNode ?? JValue.CreateNull());
                    }
                }

                return;
            }

            if (hasResult || hasError)
            {
                this.MalformedMessageReceived?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (!string.IsNullOrWhiteSpace(method) && requestIdNode == null)
            {
                if (message["params"] != null && message["params"] is not JObject)
                {
                    this.MalformedMessageReceived?.Invoke(this, EventArgs.Empty);
                    return;
                }

                this.NotificationReceived?.Invoke(
                    this,
                    new CodexNotificationEventArgs(method, message["params"] as JObject ?? new JObject()));
                return;
            }

            if (!string.IsNullOrWhiteSpace(method) && requestIdNode != null)
            {
                if (!IsValidRequestId(requestIdNode))
                {
                    this.MalformedMessageReceived?.Invoke(this, EventArgs.Empty);
                    await this.WriteAsync(
                        JsonRpcEnvelope.Error(null, -32600, "Invalid JSON-RPC request."),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (message["params"] != null && message["params"] is not JObject)
                {
                    this.MalformedMessageReceived?.Invoke(this, EventArgs.Empty);
                    await this.WriteAsync(
                        JsonRpcEnvelope.Error(requestIdNode, -32602, "Invalid request parameters."),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                var handling = this.ProcessServerRequestAsync(
                    requestIdNode,
                    method,
                    message["params"] as JObject ?? new JObject(),
                    cancellationToken);
                if (waitForHandler)
                {
                    await handling.ConfigureAwait(false);
                }
                else
                {
                    // A tool can wait for approval while other threads still need responses and notifications.
                    _ = this.ObserveHandlerAsync(handling);
                }
                return;
            }

            this.MalformedMessageReceived?.Invoke(this, EventArgs.Empty);
        }

        public void Complete(Exception? exception = null)
        {
            if (Interlocked.Exchange(ref this.completed, 1) != 0)
            {
                return;
            }

            this.readCancellation.Cancel();
            var failure = exception ?? new CodexTransportClosedException();
            foreach (var request in this.pending)
            {
                request.Value.TrySetException(failure);
            }

            this.pending.Clear();
            foreach (var request in this.incoming)
            {
                request.Value.Cancel();
            }

            foreach (var request in this.incoming)
            {
                if (this.incoming.TryRemove(request.Key, out var state))
                {
                    state.Dispose();
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            this.Complete();
            this.readCancellation.Dispose();
            this.writeGate.Dispose();
            return ValueTask.CompletedTask;
        }

        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await this.reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                    {
                        break;
                    }

                    await this.ProcessLineAsync(line, cancellationToken, waitForHandler: false).ConfigureAwait(false);
                }

                this.Complete();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                this.Complete(new CodexTransportClosedException());
            }
        }

        private async Task WriteAsync(JObject message, CancellationToken cancellationToken)
        {
            await this.writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                this.ThrowIfCompleted();
                await this.writer.WriteLineAsync(message.ToString(Formatting.None)).ConfigureAwait(false);
                await this.writer.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                this.writeGate.Release();
            }
        }

        private async Task ProcessServerRequestAsync(
            JToken requestId,
            string method,
            JObject parameters,
            CancellationToken cancellationToken)
        {
            var key = GetRequestKey(requestId);
            var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                this.readCancellation.Token,
                cancellationToken);
            var state = new IncomingRequestState(requestId, linkedCancellation);
            if (!this.incoming.TryAdd(key, state))
            {
                state.Dispose();
                await this.WriteAsync(
                    JsonRpcEnvelope.Error(requestId, -32600, "Duplicate JSON-RPC request id."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var handler = this.ServerRequestHandler;
            if (handler == null)
            {
                await this.SendErrorAsync(
                    requestId,
                    -32601,
                    "Client method not supported.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                await handler(
                    new CodexServerRequest(requestId, method, parameters),
                    linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
                await this.TrySendHandlerErrorAsync(
                    requestId,
                    -32800,
                    "Client request was cancelled.",
                    CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (Exception)
            {
                await this.TrySendHandlerErrorAsync(
                    requestId,
                    -32603,
                    "Client request handler failed.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await this.TrySendHandlerErrorAsync(
                requestId,
                -32603,
                "Client request handler did not send a response.",
                cancellationToken).ConfigureAwait(false);
        }

        private async Task CompleteIncomingRequestAsync(
            JToken requestId,
            JObject response,
            CancellationToken cancellationToken)
        {
            if (requestId == null || !IsValidRequestId(requestId))
            {
                throw new ArgumentException("A JSON string or integer request id is required.", nameof(requestId));
            }

            var key = GetRequestKey(requestId);
            if (!this.incoming.TryRemove(key, out var state))
            {
                throw new InvalidOperationException("The server request was already completed or is no longer active.");
            }

            try
            {
                await this.WriteAsync(response, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                state.Dispose();
            }
        }

        private async Task TrySendHandlerErrorAsync(
            JToken requestId,
            int code,
            string message,
            CancellationToken cancellationToken)
        {
            if (!this.incoming.ContainsKey(GetRequestKey(requestId)))
            {
                return;
            }

            try
            {
                await this.SendErrorAsync(requestId, code, message, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // 동시에 들어온 다른 응답이 정확히 한 번 완료하는 경쟁에서 먼저 처리되었습니다.
            }
            catch (CodexTransportClosedException)
            {
                // transport 종료 뒤에는 마지막 error response를 보낼 수 없습니다.
            }
        }

        private async Task ObserveHandlerAsync(Task handling)
        {
            try
            {
                await handling.ConfigureAwait(false);
            }
            catch (Exception)
            {
                this.Complete(new CodexTransportClosedException());
            }
        }

        private static bool IsValidRequestId(JToken requestId)
        {
            return requestId.Type == JTokenType.String || requestId.Type == JTokenType.Integer;
        }

        private static string GetRequestKey(JToken requestId)
        {
            return $"{requestId.Type}:{requestId.ToString(Formatting.None)}";
        }

        private void ThrowIfCompleted()
        {
            if (Volatile.Read(ref this.completed) != 0)
            {
                throw new CodexTransportClosedException();
            }
        }

        private sealed class IncomingRequestState : IDisposable
        {
            private readonly CancellationTokenSource cancellation;

            public IncomingRequestState(JToken requestId, CancellationTokenSource cancellation)
            {
                this.RequestId = requestId.DeepClone();
                this.cancellation = cancellation;
            }

            public JToken RequestId { get; }

            public void Cancel()
            {
                try
                {
                    this.cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            public void Dispose()
            {
                this.cancellation.Dispose();
            }
        }
    }
}
