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
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SmartHopper.Infrastructure.Authentication.Codex
{
    /// <summary>
    /// Owns one lazily-started Codex app-server stdio process and initialized JSON-RPC client.
    /// </summary>
    public sealed class CodexAppServerHost : ICodexAppServerHost
    {
        private readonly SemaphoreSlim lifecycleGate = new (1, 1);
        private Process? process;
        private CodexJsonRpcClient? client;
        private Task? stderrDrain;
        private CodexServerRequestHandler? serverRequestHandler;
        private bool supportsDynamicTools;
        private int disposed;

        public event EventHandler<CodexNotificationEventArgs>? NotificationReceived;

        public CodexServerRequestHandler? ServerRequestHandler
        {
            get => this.serverRequestHandler;
            set
            {
                this.serverRequestHandler = value;
                var current = this.client;
                if (current != null)
                {
                    current.ServerRequestHandler = value;
                }
            }
        }

        public bool IsRunning
        {
            get
            {
                var current = this.process;
                if (current == null)
                {
                    return false;
                }

                try
                {
                    return !current.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }

        public bool SupportsDynamicTools => this.IsRunning && this.supportsDynamicTools;

        public async Task StartAsync(CodexLaunchOptions options, CancellationToken cancellationToken = default)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            await this.lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                this.ThrowIfDisposed();
                if (this.IsRunning && this.client != null)
                {
                    return;
                }

                var startedProcess = new Process
                {
                    StartInfo = CodexProcessStartInfo.Create(
                        options.ExecutablePath,
                        new[] { "app-server", "--listen", "stdio://" },
                        options.CodexHome),
                    EnableRaisingEvents = true,
                };

                if (!startedProcess.Start())
                {
                    startedProcess.Dispose();
                    throw new InvalidOperationException("Codex app-server를 시작하지 못했습니다.");
                }

                var startedClient = new CodexJsonRpcClient(startedProcess.StandardOutput, startedProcess.StandardInput);
                startedClient.ServerRequestHandler = this.serverRequestHandler;
                startedClient.NotificationReceived += this.OnNotificationReceived;
                startedProcess.Exited += this.OnProcessExited;
                this.process = startedProcess;
                this.client = startedClient;
                this.stderrDrain = DrainDiagnosticsAsync(startedProcess);
                startedClient.Start();

                try
                {
                    try
                    {
                        await startedClient.SendRequestAsync(
                            "initialize",
                            CreateInitializeParameters(experimentalApi: true),
                            TimeSpan.FromSeconds(15),
                            cancellationToken).ConfigureAwait(false);
                        this.supportsDynamicTools = true;
                    }
                    catch (CodexJsonRpcException ex) when (ex.Code == -32601 || ex.Code == -32602)
                    {
                        await startedClient.SendRequestAsync(
                            "initialize",
                            CreateInitializeParameters(experimentalApi: false),
                            TimeSpan.FromSeconds(15),
                            cancellationToken).ConfigureAwait(false);
                        this.supportsDynamicTools = false;
                    }

                    await startedClient.SendNotificationAsync("initialized", cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    this.AbortCore();
                    startedClient.NotificationReceived -= this.OnNotificationReceived;
                    startedProcess.Exited -= this.OnProcessExited;
                    await startedClient.DisposeAsync().ConfigureAwait(false);
                    startedProcess.Dispose();
                    this.process = null;
                    this.client = null;
                    this.stderrDrain = null;
                    throw;
                }
            }
            finally
            {
                this.lifecycleGate.Release();
            }
        }

        public Task<JToken> SendRequestAsync(
            string method,
            JObject? parameters = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            var current = this.client;
            if (current == null || !this.IsRunning)
            {
                throw new CodexTransportClosedException();
            }

            return current.SendRequestAsync(method, parameters, timeout, cancellationToken);
        }

        public Task SendResponseAsync(
            JToken requestId,
            JToken? result,
            CancellationToken cancellationToken = default)
        {
            var current = this.client;
            if (current == null || !this.IsRunning)
            {
                throw new CodexTransportClosedException();
            }

            return current.SendResponseAsync(requestId, result, cancellationToken);
        }

        public Task SendErrorAsync(
            JToken requestId,
            int code,
            string message,
            CancellationToken cancellationToken = default)
        {
            var current = this.client;
            if (current == null || !this.IsRunning)
            {
                throw new CodexTransportClosedException();
            }

            return current.SendErrorAsync(requestId, code, message, cancellationToken);
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await this.lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var currentProcess = this.process;
                var currentClient = this.client;
                if (currentProcess == null)
                {
                    return;
                }

                TryCloseStandardInput(currentProcess);
                if (!HasExited(currentProcess))
                {
                    var exited = currentProcess.WaitForExitAsync(CancellationToken.None);
                    var gracePeriod = Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
                    if (await Task.WhenAny(exited, gracePeriod).ConfigureAwait(false) != exited)
                    {
                        TryKill(currentProcess);
                        await currentProcess.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }

                currentClient?.Complete();
                if (currentClient != null)
                {
                    currentClient.NotificationReceived -= this.OnNotificationReceived;
                    await currentClient.DisposeAsync().ConfigureAwait(false);
                }

                currentProcess.Exited -= this.OnProcessExited;
                currentProcess.Dispose();
                this.process = null;
                this.client = null;
                this.stderrDrain = null;
                this.supportsDynamicTools = false;
            }
            finally
            {
                this.lifecycleGate.Release();
            }
        }

        public void BeginShutdown()
        {
            var current = this.process;
            if (current != null)
            {
                TryCloseStandardInput(current);
            }

            _ = this.StopAsync(CancellationToken.None);
        }

        public void Abort()
        {
            this.AbortCore();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            {
                return;
            }

            await this.StopAsync(CancellationToken.None).ConfigureAwait(false);
            this.lifecycleGate.Dispose();
        }

        private static async Task DrainDiagnosticsAsync(Process process)
        {
            try
            {
                while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) != null)
                {
                    // Keep stderr separate from the JSONL protocol. Raw diagnostics are deliberately
                    // discarded because an authentication URL or other sensitive value may appear.
                    Debug.WriteLine("[Codex] app-server diagnostic received.");
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static string GetAssemblyVersion()
        {
            return typeof(CodexAppServerHost).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(CodexAppServerHost).Assembly.GetName().Version?.ToString()
                ?? "0.0.0";
        }

        private static JObject CreateInitializeParameters(bool experimentalApi)
        {
            return new JObject
            {
                ["clientInfo"] = new JObject
                {
                    ["name"] = "SmartHopper",
                    ["title"] = "SmartHopper",
                    ["version"] = GetAssemblyVersion(),
                },
                ["capabilities"] = new JObject
                {
                    ["experimentalApi"] = experimentalApi,
                },
            };
        }

        private static bool HasExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        private static void TryCloseStandardInput(Process process)
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void OnNotificationReceived(object? sender, CodexNotificationEventArgs e)
        {
            this.NotificationReceived?.Invoke(this, e);
        }

        private void OnProcessExited(object? sender, EventArgs e)
        {
            this.client?.Complete(new CodexTransportClosedException());
        }

        private void AbortCore()
        {
            var current = this.process;
            if (current != null)
            {
                TryKill(current);
            }

            this.client?.Complete(new CodexTransportClosedException());
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref this.disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(CodexAppServerHost));
            }
        }
    }
}
