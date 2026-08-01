/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public License.
 */

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SmartHopper.Infrastructure.Authentication.Codex
{
    /// <summary>
    /// Newline-delimited JSON-RPC client for Codex app-server stdio.
    /// </summary>
    public sealed class CodexJsonRpcClient : IAsyncDisposable
    {
        private const string JsonRpcVersion = "2.0";
        private readonly TextReader reader;
        private readonly TextWriter writer;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JToken>> pending = new ();
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
                    ["jsonrpc"] = JsonRpcVersion,
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
                ["jsonrpc"] = JsonRpcVersion,
                ["method"] = method,
            };
            if (parameters != null)
            {
                notification["params"] = parameters;
            }

            return this.WriteAsync(notification, cancellationToken);
        }

        /// <summary>
        /// Processes one JSONL message. Public for deterministic transport tests.
        /// </summary>
        public async Task ProcessLineAsync(string line, CancellationToken cancellationToken = default)
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

            var requestIdNode = message["id"];
            var method = message.Value<string>("method");
            if (requestIdNode != null && (message["result"] != null || message["error"] != null))
            {
                if (requestIdNode.Type == JTokenType.Integer && this.pending.TryGetValue(requestIdNode.Value<long>(), out var completion))
                {
                    if (message["error"] is JObject error)
                    {
                        completion.TrySetException(new CodexJsonRpcException(error.Value<int?>("code") ?? -32603));
                    }
                    else
                    {
                        completion.TrySetResult(message["result"] ?? JValue.CreateNull());
                    }
                }

                return;
            }

            if (!string.IsNullOrWhiteSpace(method) && requestIdNode == null)
            {
                this.NotificationReceived?.Invoke(
                    this,
                    new CodexNotificationEventArgs(method, message["params"] as JObject ?? new JObject()));
                return;
            }

            if (!string.IsNullOrWhiteSpace(method) && requestIdNode != null)
            {
                var unsupportedResponse = new JObject
                {
                    ["jsonrpc"] = JsonRpcVersion,
                    ["id"] = requestIdNode,
                    ["error"] = new JObject
                    {
                        ["code"] = -32601,
                        ["message"] = "Client method not supported.",
                    },
                };
                await this.WriteAsync(unsupportedResponse, cancellationToken).ConfigureAwait(false);
            }
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

                    await this.ProcessLineAsync(line, cancellationToken).ConfigureAwait(false);
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

        private void ThrowIfCompleted()
        {
            if (Volatile.Read(ref this.completed) != 0)
            {
                throw new CodexTransportClosedException();
            }
        }
    }
}
