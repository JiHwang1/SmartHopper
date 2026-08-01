/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 * Licensed under the GNU Lesser General Public License.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SmartHopper.Infrastructure.Authentication.Codex;
using Xunit;

namespace SmartHopper.Infrastructure.Tests.Authentication
{
    public class CodexAccountServiceTests
    {
        [Fact]
        public async Task LoginCompletedNotification_RefreshesAccount()
        {
            var host = new FakeHost();
            var browser = new FakeBrowser();
            await using var service = BuildService(host, browser);
            await service.ConfigureAsync(null, null);
            var login = await service.StartChatGptLoginAsync();
            Assert.True(login.BrowserOpened);
            Assert.Equal(CodexAccountStatus.LoginPending, service.CurrentState.Status);

            host.Account = SignedInAccount();
            host.Raise("account/login/completed", new JObject { ["success"] = true, ["loginId"] = "login-1" });
            await WaitForStateAsync(service, CodexAccountStatus.SignedIn);

            Assert.Equal(1, host.AccountReadCount);
            Assert.Equal("person@example.com", service.CurrentState.Email);
            Assert.Equal("plus", service.CurrentState.PlanType);
        }

        [Fact]
        public async Task LoginFailure_MovesStateToError()
        {
            var host = new FakeHost();
            await using var service = BuildService(host, new FakeBrowser());
            await service.ConfigureAsync(null, null);
            await service.StartChatGptLoginAsync();

            host.Raise("account/login/completed", new JObject { ["success"] = false, ["error"] = "private server detail" });
            await WaitForStateAsync(service, CodexAccountStatus.Error);

            Assert.Null(service.CurrentState.CurrentLoginId);
            Assert.DoesNotContain("private server detail", service.CurrentState.UserMessage ?? string.Empty);
        }

        [Fact]
        public async Task CancelLogin_ClearsPendingState()
        {
            var host = new FakeHost();
            await using var service = BuildService(host, new FakeBrowser());
            await service.ConfigureAsync(null, null);
            await service.StartChatGptLoginAsync();

            await service.CancelLoginAsync();

            Assert.Equal(CodexAccountStatus.SignedOut, service.CurrentState.Status);
            Assert.Null(service.CurrentState.CurrentLoginId);
            Assert.Equal(1, host.CancelCount);
        }

        [Fact]
        public async Task Logout_MovesStateToSignedOut()
        {
            var host = new FakeHost { Account = SignedInAccount() };
            await using var service = BuildService(host, new FakeBrowser());
            await service.ConfigureAsync(null, null);
            await service.RefreshAsync();
            Assert.Equal(CodexAccountStatus.SignedIn, service.CurrentState.Status);

            await service.LogoutAsync();

            Assert.Equal(CodexAccountStatus.SignedOut, service.CurrentState.Status);
            Assert.Null(service.CurrentState.Email);
            Assert.Equal(1, host.LogoutCount);
        }

        [Fact]
        public async Task Refresh_ReadsRemainingLimitsAndResetTimes()
        {
            var host = new FakeHost { Account = SignedInAccount() };
            await using var service = BuildService(host, new FakeBrowser());
            await service.ConfigureAsync(null, null);

            await service.RefreshAsync();

            Assert.Equal(73, service.CurrentState.PrimaryRateLimit?.RemainingPercent);
            Assert.Equal(300, service.CurrentState.PrimaryRateLimit?.WindowDurationMinutes);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785552000).ToLocalTime(), service.CurrentState.PrimaryRateLimit?.ResetsAt);
            Assert.Equal(41, service.CurrentState.SecondaryRateLimit?.RemainingPercent);
        }

        [Fact]
        public async Task PreferredLoginFields_Rejected_RetriesMinimalRequest()
        {
            var host = new FakeHost { RejectPreferredLoginOnce = true };
            await using var service = BuildService(host, new FakeBrowser());
            await service.ConfigureAsync(null, null);

            var result = await service.StartChatGptLoginAsync();

            Assert.True(result.BrowserOpened);
            Assert.Equal(2, host.LoginStartCount);
            Assert.Equal(new[] { 3, 1 }, host.LoginPropertyCounts);
        }

        [Fact]
        public async Task NonHttpsLoginAddress_IsNotOpened()
        {
            var host = new FakeHost { LoginAddress = "http://example.com/login?private=value" };
            var browser = new FakeBrowser();
            await using var service = BuildService(host, browser);
            await service.ConfigureAsync(null, null);

            var result = await service.StartChatGptLoginAsync();

            Assert.False(result.BrowserOpened);
            Assert.Equal(0, browser.OpenCount);
            Assert.Equal(CodexAccountStatus.Error, service.CurrentState.Status);
            Assert.DoesNotContain("private=value", service.CurrentState.UserMessage ?? string.Empty);
        }

        private static CodexAccountService BuildService(FakeHost host, FakeBrowser browser)
        {
            return new CodexAccountService(new FixedLocator(), host, browser);
        }

        private static JObject SignedInAccount()
        {
            return new JObject
            {
                ["type"] = "chatgpt",
                ["email"] = "person@example.com",
                ["planType"] = "plus",
            };
        }

        private static async Task WaitForStateAsync(ICodexAccountService service, CodexAccountStatus expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (service.CurrentState.Status == expected)
                {
                    return;
                }

                await Task.Delay(10);
            }

            Assert.Equal(expected, service.CurrentState.Status);
        }

        private sealed class FixedLocator : ICodexExecutableLocator
        {
            public Task<CodexExecutableInfo> LocateAsync(string? configuredPath, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new CodexExecutableInfo
                {
                    IsAvailable = true,
                    Path = configuredPath ?? "codex-test",
                    Version = "codex-cli test",
                });
            }
        }

        private sealed class FakeBrowser : ICodexBrowserLauncher
        {
            public int OpenCount { get; private set; }

            public void Open(Uri uri)
            {
                Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
                this.OpenCount++;
            }
        }

        private sealed class FakeHost : ICodexAppServerHost
        {
            public event EventHandler<CodexNotificationEventArgs>? NotificationReceived;

            public bool IsRunning { get; private set; }

            public JObject? Account { get; set; }

            public string LoginAddress { get; set; } = "https://auth.example.com/login?opaque=hidden";

            public bool RejectPreferredLoginOnce { get; set; }

            public int AccountReadCount { get; private set; }

            public int LoginStartCount { get; private set; }

            public int CancelCount { get; private set; }

            public int LogoutCount { get; private set; }

            public List<int> LoginPropertyCounts { get; } = new ();

            public Task StartAsync(CodexLaunchOptions options, CancellationToken cancellationToken = default)
            {
                this.IsRunning = true;
                return Task.CompletedTask;
            }

            public Task<JToken> SendRequestAsync(
                string method,
                JObject? parameters = null,
                TimeSpan? timeout = null,
                CancellationToken cancellationToken = default)
            {
                switch (method)
                {
                    case "account/read":
                        this.AccountReadCount++;
                        return Task.FromResult<JToken>(new JObject
                        {
                            ["account"] = (JToken?)this.Account ?? JValue.CreateNull(),
                            ["requiresOpenaiAuth"] = true,
                        });
                    case "account/login/start":
                        this.LoginStartCount++;
                        this.LoginPropertyCounts.Add(parameters?.Count ?? 0);
                        if (this.RejectPreferredLoginOnce && this.LoginStartCount == 1)
                        {
                            throw new CodexJsonRpcException(-32602);
                        }

                        return Task.FromResult<JToken>(new JObject
                        {
                            ["type"] = "chatgpt",
                            ["loginId"] = "login-1",
                            ["authUrl"] = this.LoginAddress,
                        });
                    case "account/rateLimits/read":
                        return Task.FromResult<JToken>(new JObject
                        {
                            ["rateLimits"] = new JObject
                            {
                                ["planType"] = "plus",
                                ["primary"] = new JObject
                                {
                                    ["usedPercent"] = 27,
                                    ["windowDurationMins"] = 300,
                                    ["resetsAt"] = 1785552000,
                                },
                                ["secondary"] = new JObject
                                {
                                    ["usedPercent"] = 59,
                                    ["windowDurationMins"] = 10080,
                                    ["resetsAt"] = 1786000000,
                                },
                            },
                        });
                    case "account/login/cancel":
                        this.CancelCount++;
                        return Task.FromResult<JToken>(new JObject { ["status"] = "canceled" });
                    case "account/logout":
                        this.LogoutCount++;
                        this.Account = null;
                        return Task.FromResult<JToken>(new JObject());
                    default:
                        throw new InvalidOperationException(method);
                }
            }

            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                this.IsRunning = false;
                return Task.CompletedTask;
            }

            public void BeginShutdown()
            {
                this.IsRunning = false;
            }

            public void Abort()
            {
                this.IsRunning = false;
            }

            public ValueTask DisposeAsync()
            {
                this.IsRunning = false;
                return ValueTask.CompletedTask;
            }

            public void Raise(string method, JObject parameters)
            {
                this.NotificationReceived?.Invoke(this, new CodexNotificationEventArgs(method, parameters));
            }
        }
    }
}
