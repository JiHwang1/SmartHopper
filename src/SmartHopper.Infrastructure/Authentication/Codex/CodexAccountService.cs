/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public License.
 */

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SmartHopper.Infrastructure.Authentication.Codex
{
    /// <summary>
    /// Account-only facade over the stable Codex app-server account protocol.
    /// </summary>
    public sealed class CodexAccountService : ICodexAccountService, ICodexConversationTransport, IAsyncDisposable
    {
        private readonly ICodexExecutableLocator locator;
        private readonly ICodexAppServerHost host;
        private readonly ICodexBrowserLauncher browser;
        private readonly SemaphoreSlim operationGate = new (1, 1);
        private CodexAccountState currentState = new ();
        private CodexExecutableInfo? executable;
        private string? configuredPath;
        private string? codexHome;
        private int disposed;

        public CodexAccountService(
            ICodexExecutableLocator locator,
            ICodexAppServerHost host,
            ICodexBrowserLauncher browser)
        {
            this.locator = locator ?? throw new ArgumentNullException(nameof(locator));
            this.host = host ?? throw new ArgumentNullException(nameof(host));
            this.browser = browser ?? throw new ArgumentNullException(nameof(browser));
            this.host.NotificationReceived += this.OnNotificationReceived;
        }

        public event EventHandler<CodexAccountStateChangedEventArgs>? StateChanged;

        event EventHandler<CodexNotificationEventArgs>? ICodexConversationTransport.NotificationReceived
        {
            add => this.host.NotificationReceived += value;
            remove => this.host.NotificationReceived -= value;
        }

        public CodexAccountState CurrentState => Volatile.Read(ref this.currentState);

        public async Task ConfigureAsync(string? executablePath, string? codexHome, CancellationToken cancellationToken = default)
        {
            await this.operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                this.ThrowIfDisposed();
                var normalizedPath = NormalizeOptional(executablePath);
                var normalizedHome = NormalizeOptional(codexHome);
                var changed = !string.Equals(this.configuredPath, normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(this.codexHome, normalizedHome, StringComparison.OrdinalIgnoreCase);
                if (!changed && this.executable != null)
                {
                    return;
                }

                if (changed && this.host.IsRunning)
                {
                    await this.host.StopAsync(cancellationToken).ConfigureAwait(false);
                }

                this.configuredPath = normalizedPath;
                this.codexHome = normalizedHome;
                this.executable = await this.locator.LocateAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
                if (!this.executable.IsAvailable)
                {
                    this.Publish(new CodexAccountState
                    {
                        Status = CodexAccountStatus.CodexNotFound,
                        UserMessage = this.executable.UserMessage,
                    });
                    return;
                }

                this.Publish(new CodexAccountState
                {
                    Status = CodexAccountStatus.SignedOut,
                    ExecutablePath = this.executable.Path,
                    CodexVersion = this.executable.Version,
                    ServerConnected = this.host.IsRunning,
                });
            }
            finally
            {
                this.operationGate.Release();
            }
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            await this.operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await this.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                await this.ReadAccountAsync(refreshToken: false, cancellationToken).ConfigureAwait(false);
                if (this.CurrentState.Status == CodexAccountStatus.SignedIn)
                {
                    await this.ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (this.CurrentState.Status != CodexAccountStatus.CodexNotFound)
                {
                    this.PublishError(ex);
                }
            }
            finally
            {
                this.operationGate.Release();
            }
        }

        public async Task<CodexAccountState> RefreshAsync(bool refreshToken = false, CancellationToken cancellationToken = default)
        {
            await this.operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await this.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                await this.ReadAccountAsync(refreshToken, cancellationToken).ConfigureAwait(false);
                if (this.CurrentState.Status == CodexAccountStatus.SignedIn)
                {
                    await this.ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (this.CurrentState.Status != CodexAccountStatus.CodexNotFound)
                {
                    this.PublishError(ex);
                }
            }
            finally
            {
                this.operationGate.Release();
            }

            return this.CurrentState;
        }

        public async Task<CodexAccountState> RefreshRateLimitsAsync(CancellationToken cancellationToken = default)
        {
            await this.operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await this.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                await this.ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                this.PublishError(ex);
            }
            finally
            {
                this.operationGate.Release();
            }

            return this.CurrentState;
        }

        public async Task<CodexLoginStartResult> StartChatGptLoginAsync(CancellationToken cancellationToken = default)
        {
            await this.operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!string.IsNullOrWhiteSpace(this.CurrentState.CurrentLoginId))
                {
                    return new CodexLoginStartResult
                    {
                        LoginId = this.CurrentState.CurrentLoginId,
                        UserMessage = "ChatGPT 로그인이 이미 진행 중입니다.",
                    };
                }

                await this.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                this.PublishCopy(isBusy: true, userMessage: null);

                JToken response;
                try
                {
                    response = await this.host.SendRequestAsync(
                        "account/login/start",
                        new JObject
                        {
                            ["type"] = "chatgpt",
                            ["useHostedLoginSuccessPage"] = true,
                            ["appBrand"] = "codex",
                        },
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (CodexJsonRpcException ex) when (ex.Code == -32602)
                {
                    response = await this.host.SendRequestAsync(
                        "account/login/start",
                        new JObject { ["type"] = "chatgpt" },
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                var responseObject = response as JObject;
                var loginId = responseObject?.Value<string>("loginId");
                var rawAddress = responseObject?.Value<string>("authUrl");
                if (string.IsNullOrWhiteSpace(loginId) ||
                    !Uri.TryCreate(rawAddress, UriKind.Absolute, out var address) ||
                    !string.Equals(address.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    this.Publish(new CodexAccountState
                    {
                        Status = CodexAccountStatus.Error,
                        ExecutablePath = this.executable?.Path,
                        CodexVersion = this.executable?.Version,
                        ServerConnected = this.host.IsRunning,
                        UserMessage = "Codex가 안전한 HTTPS 로그인 주소를 반환하지 않았습니다.",
                    });
                    return new CodexLoginStartResult { UserMessage = this.CurrentState.UserMessage };
                }

                try
                {
                    this.browser.Open(address);
                }
                catch
                {
                    this.Publish(new CodexAccountState
                    {
                        Status = CodexAccountStatus.Error,
                        ExecutablePath = this.executable?.Path,
                        CodexVersion = this.executable?.Version,
                        ServerConnected = this.host.IsRunning,
                        CurrentLoginId = loginId,
                        UserMessage = "기본 브라우저를 열지 못했습니다. 로그인을 취소한 뒤 다시 시도하세요.",
                    });
                    return new CodexLoginStartResult
                    {
                        LoginId = loginId,
                        UserMessage = this.CurrentState.UserMessage,
                    };
                }

                this.Publish(new CodexAccountState
                {
                    Status = CodexAccountStatus.LoginPending,
                    ExecutablePath = this.executable?.Path,
                    CodexVersion = this.executable?.Version,
                    ServerConnected = true,
                    CurrentLoginId = loginId,
                    UserMessage = "브라우저에서 ChatGPT 로그인을 완료하세요.",
                });
                return new CodexLoginStartResult
                {
                    BrowserOpened = true,
                    LoginId = loginId,
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (this.CurrentState.Status != CodexAccountStatus.CodexNotFound)
                {
                    this.PublishError(ex);
                }

                return new CodexLoginStartResult { UserMessage = this.CurrentState.UserMessage };
            }
            finally
            {
                this.operationGate.Release();
            }
        }

        public async Task CancelLoginAsync(CancellationToken cancellationToken = default)
        {
            await this.operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var loginId = this.CurrentState.CurrentLoginId;
                if (string.IsNullOrWhiteSpace(loginId))
                {
                    return;
                }

                this.PublishCopy(isBusy: true, userMessage: null);
                await this.host.SendRequestAsync(
                    "account/login/cancel",
                    new JObject { ["loginId"] = loginId },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                this.PublishSignedOut("로그인 진행을 취소했습니다.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                this.PublishError(ex);
            }
            finally
            {
                this.operationGate.Release();
            }
        }

        public async Task LogoutAsync(CancellationToken cancellationToken = default)
        {
            await this.operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await this.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                this.PublishCopy(isBusy: true, userMessage: null);
                await this.host.SendRequestAsync("account/logout", new JObject(), cancellationToken: cancellationToken).ConfigureAwait(false);
                this.PublishSignedOut("Codex 계정에서 로그아웃했습니다.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                this.PublishError(ex);
            }
            finally
            {
                this.operationGate.Release();
            }
        }

        public async Task EnsureSignedInAsync(CancellationToken cancellationToken = default)
        {
            if (this.CurrentState.Status != CodexAccountStatus.SignedIn || !this.host.IsRunning)
            {
                await this.RefreshAsync(refreshToken: false, cancellationToken).ConfigureAwait(false);
            }

            if (this.CurrentState.Status != CodexAccountStatus.SignedIn)
            {
                throw new InvalidOperationException("Codex Account 탭에서 ChatGPT 로그인을 먼저 완료하세요.");
            }
        }

        public async Task<JToken> SendAsync(
            string method,
            JObject? parameters = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            await this.EnsureSignedInAsync(cancellationToken).ConfigureAwait(false);
            return await this.host.SendRequestAsync(method, parameters, timeout, cancellationToken).ConfigureAwait(false);
        }

        public void BeginShutdown()
        {
            this.host.BeginShutdown();
        }

        public void Abort()
        {
            this.host.Abort();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            {
                return;
            }

            this.host.NotificationReceived -= this.OnNotificationReceived;
            await this.host.DisposeAsync().ConfigureAwait(false);
            this.operationGate.Dispose();
        }

        private static string? NormalizeOptional(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            this.ThrowIfDisposed();
            if (this.executable?.IsAvailable != true || string.IsNullOrWhiteSpace(this.executable.Path))
            {
                this.executable = await this.locator.LocateAsync(this.configuredPath, cancellationToken).ConfigureAwait(false);
            }

            if (this.executable?.IsAvailable != true || string.IsNullOrWhiteSpace(this.executable.Path))
            {
                this.Publish(new CodexAccountState
                {
                    Status = CodexAccountStatus.CodexNotFound,
                    UserMessage = this.executable?.UserMessage,
                });
                throw new InvalidOperationException("Codex is not available.");
            }

            if (this.host.IsRunning)
            {
                return;
            }

            this.Publish(new CodexAccountState
            {
                Status = CodexAccountStatus.StartingServer,
                ExecutablePath = this.executable.Path,
                CodexVersion = this.executable.Version,
                IsBusy = true,
            });
            await this.host.StartAsync(
                new CodexLaunchOptions
                {
                    ExecutablePath = this.executable.Path,
                    CodexHome = this.codexHome,
                },
                cancellationToken).ConfigureAwait(false);
        }

        private async Task ReadAccountAsync(bool refreshToken, CancellationToken cancellationToken)
        {
            this.PublishCopy(isBusy: true, userMessage: null);
            var response = await this.host.SendRequestAsync(
                "account/read",
                new JObject { ["refreshToken"] = refreshToken },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var responseObject = response as JObject;
            var account = responseObject?["account"] as JObject;
            if (account == null)
            {
                this.PublishSignedOut(null, DateTimeOffset.Now);
                return;
            }

            var mode = account.Value<string>("type");
            this.Publish(new CodexAccountState
            {
                Status = CodexAccountStatus.SignedIn,
                ExecutablePath = this.executable?.Path,
                CodexVersion = this.executable?.Version,
                ServerConnected = true,
                Email = account.Value<string>("email"),
                PlanType = account.Value<string>("planType"),
                AuthenticationMode = ToDisplayAuthMode(mode),
                LastRefreshedAt = DateTimeOffset.Now,
            });
        }

        private async Task ReadRateLimitsAsync(CancellationToken cancellationToken)
        {
            var response = await this.host.SendRequestAsync(
                "account/rateLimits/read",
                new JObject(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var snapshot = (response as JObject)?["rateLimits"] as JObject;
            var previous = this.CurrentState;
            this.Publish(new CodexAccountState
            {
                Status = previous.Status,
                ExecutablePath = previous.ExecutablePath,
                CodexVersion = previous.CodexVersion,
                ServerConnected = this.host.IsRunning,
                Email = previous.Email,
                PlanType = snapshot?.Value<string>("planType") ?? previous.PlanType,
                AuthenticationMode = previous.AuthenticationMode,
                CurrentLoginId = previous.CurrentLoginId,
                PrimaryRateLimit = ParseRateLimitWindow(snapshot?["primary"] as JObject),
                SecondaryRateLimit = ParseRateLimitWindow(snapshot?["secondary"] as JObject),
                LastRefreshedAt = DateTimeOffset.Now,
            });
        }

        private static CodexRateLimitWindow? ParseRateLimitWindow(JObject? value)
        {
            if (value == null)
            {
                return null;
            }

            DateTimeOffset? resetsAt = null;
            var resetSeconds = value.Value<long?>("resetsAt");
            if (resetSeconds.HasValue)
            {
                try
                {
                    resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetSeconds.Value).ToLocalTime();
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }

            return new CodexRateLimitWindow
            {
                UsedPercent = Math.Clamp(value.Value<int?>("usedPercent") ?? 0, 0, 100),
                WindowDurationMinutes = value.Value<long?>("windowDurationMins"),
                ResetsAt = resetsAt,
            };
        }

        private static string ToDisplayAuthMode(string? mode)
        {
            return mode switch
            {
                "chatgpt" => "ChatGPT OAuth (Codex 관리)",
                "apiKey" => "Codex API key 계정",
                "amazonBedrock" => "Amazon Bedrock",
                _ => mode ?? "알 수 없음",
            };
        }

        private void OnNotificationReceived(object? sender, CodexNotificationEventArgs e)
        {
            _ = this.HandleNotificationAsync(e);
        }

        private async Task HandleNotificationAsync(CodexNotificationEventArgs notification)
        {
            try
            {
                if (notification.Method == "account/login/completed")
                {
                    if (notification.Parameters.Value<bool?>("success") == true)
                    {
                        await this.RefreshAsync(refreshToken: false).ConfigureAwait(false);
                    }
                    else
                    {
                        this.Publish(new CodexAccountState
                        {
                            Status = CodexAccountStatus.Error,
                            ExecutablePath = this.executable?.Path,
                            CodexVersion = this.executable?.Version,
                            ServerConnected = this.host.IsRunning,
                            UserMessage = "ChatGPT 로그인에 실패했습니다. 다시 시도하세요.",
                        });
                    }
                }
                else if (notification.Method == "account/updated")
                {
                    await this.RefreshAsync(refreshToken: false).ConfigureAwait(false);
                }
                else if (notification.Method == "account/rateLimits/updated")
                {
                    await this.RefreshRateLimitsAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void PublishSignedOut(string? message, DateTimeOffset? refreshedAt = null)
        {
            this.Publish(new CodexAccountState
            {
                Status = CodexAccountStatus.SignedOut,
                ExecutablePath = this.executable?.Path,
                CodexVersion = this.executable?.Version,
                ServerConnected = this.host.IsRunning,
                UserMessage = message,
                LastRefreshedAt = refreshedAt,
            });
        }

        private void PublishCopy(bool isBusy, string? userMessage)
        {
            var previous = this.CurrentState;
            this.Publish(new CodexAccountState
            {
                Status = previous.Status,
                ExecutablePath = previous.ExecutablePath,
                CodexVersion = previous.CodexVersion,
                ServerConnected = this.host.IsRunning,
                IsBusy = isBusy,
                Email = previous.Email,
                PlanType = previous.PlanType,
                AuthenticationMode = previous.AuthenticationMode,
                PrimaryRateLimit = previous.PrimaryRateLimit,
                SecondaryRateLimit = previous.SecondaryRateLimit,
                CurrentLoginId = previous.CurrentLoginId,
                UserMessage = userMessage,
                LastRefreshedAt = previous.LastRefreshedAt,
            });
        }

        private void PublishError(Exception exception)
        {
            var previous = this.CurrentState;
            var message = exception switch
            {
                TimeoutException => "Codex app-server 응답 시간이 초과되었습니다.",
                CodexJsonRpcException rpc => $"Codex 요청이 거부되었습니다 (오류 코드 {rpc.Code}).",
                _ => "Codex app-server와 통신하지 못했습니다. Codex 경로와 상태를 확인하세요.",
            };
            this.Publish(new CodexAccountState
            {
                Status = CodexAccountStatus.Error,
                ExecutablePath = this.executable?.Path ?? previous.ExecutablePath,
                CodexVersion = this.executable?.Version ?? previous.CodexVersion,
                ServerConnected = this.host.IsRunning,
                CurrentLoginId = previous.CurrentLoginId,
                Email = previous.Email,
                PlanType = previous.PlanType,
                AuthenticationMode = previous.AuthenticationMode,
                PrimaryRateLimit = previous.PrimaryRateLimit,
                SecondaryRateLimit = previous.SecondaryRateLimit,
                UserMessage = message,
                LastRefreshedAt = previous.LastRefreshedAt,
            });
        }

        private void Publish(CodexAccountState state)
        {
            Volatile.Write(ref this.currentState, state);
            this.StateChanged?.Invoke(this, new CodexAccountStateChangedEventArgs(state));
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref this.disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(CodexAccountService));
            }
        }
    }

    /// <summary>
    /// Default system browser launcher. Only the account service supplies validated HTTPS URIs.
    /// </summary>
    public sealed class CodexSystemBrowserLauncher : ICodexBrowserLauncher
    {
        public void Open(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("Only HTTPS authentication addresses are allowed.");
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
    }
}
