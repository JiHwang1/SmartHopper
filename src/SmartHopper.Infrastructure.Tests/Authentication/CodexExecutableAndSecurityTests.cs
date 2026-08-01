/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 * Licensed under the GNU Lesser General Public License.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SmartHopper.Infrastructure.Authentication.Codex;
using SmartHopper.Infrastructure.Settings;
using Xunit;

namespace SmartHopper.Infrastructure.Tests.Authentication
{
    public class CodexExecutableAndSecurityTests
    {
        [Fact]
        public async Task WindowsPathSearch_FindsCommandShim()
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                @"C:\Users\person\npm\codex.cmd",
            };
            var probe = new FakeProbe();
            var locator = new CodexExecutableLocator(
                isWindows: true,
                pathValue: @"C:\Tools;C:\Users\person\npm",
                existing.Contains,
                probe);

            var result = await locator.LocateAsync(null);

            Assert.True(result.IsAvailable);
            Assert.Equal(@"C:\Users\person\npm\codex.cmd", result.Path);
            Assert.Equal(result.Path, probe.LastPath);
        }

        [Fact]
        public async Task UnixPathSearch_FindsExecutableWithoutWindowsShimNames()
        {
            var probe = new FakeProbe();
            var locator = new CodexExecutableLocator(
                isWindows: false,
                pathValue: "/opt/bin:/usr/local/bin",
                path => path == "/usr/local/bin/codex",
                probe);

            var result = await locator.LocateAsync(null);

            Assert.True(result.IsAvailable);
            Assert.Equal("/usr/local/bin/codex", result.Path);
        }

        [Fact]
        public async Task ConfiguredPath_TakesPriorityOverPathSearch()
        {
            var probe = new FakeProbe();
            var locator = new CodexExecutableLocator(
                isWindows: true,
                pathValue: @"C:\Other",
                path => path == @"C:\Chosen\codex.exe",
                probe);

            var result = await locator.LocateAsync(@"C:\Chosen\codex.exe");

            Assert.Equal(@"C:\Chosen\codex.exe", result.Path);
            Assert.Equal(@"C:\Chosen\codex.exe", probe.LastPath);
        }

        [Fact]
        public async Task WindowsCommandShim_IsExecutedByTheRealProbe()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            var shimPath = Path.Combine(Path.GetTempPath(), $"smarthopper-codex-{Guid.NewGuid():N}.cmd");
            try
            {
                File.WriteAllText(shimPath, "@echo off\r\nif \"%1\"==\"--version\" echo codex-cli test\r\n");

                var result = await new CodexExecutableProbe().ProbeAsync(shimPath);

                Assert.True(result.Success);
                Assert.Equal("codex-cli test", result.Version);
            }
            finally
            {
                File.Delete(shimPath);
            }
        }

        [Fact]
        public void ProcessStartInfo_UsesBomlessUtf8ForRedirectedProtocolStreams()
        {
            var startInfo = CodexProcessStartInfo.Create(
                Path.Combine(Path.GetTempPath(), "codex.exe"),
                new[] { "app-server", "--listen", "stdio://" },
                null);

            Assert.Equal(Encoding.UTF8.WebName, startInfo.StandardInputEncoding?.WebName);
            Assert.Equal(Encoding.UTF8.WebName, startInfo.StandardOutputEncoding?.WebName);
            Assert.Equal(Encoding.UTF8.WebName, startInfo.StandardErrorEncoding?.WebName);
            Assert.Empty(startInfo.StandardInputEncoding?.GetPreamble() ?? Array.Empty<byte>());
        }

        [Fact]
        public void UiPolicy_TracksLoginAndBusyStates()
        {
            var signedOut = CodexAccountUiState.From(new CodexAccountState
            {
                Status = CodexAccountStatus.SignedOut,
                ExecutablePath = @"C:\codex.exe",
            });
            Assert.True(signedOut.CanConnect);
            Assert.True(signedOut.CanSignIn);
            Assert.False(signedOut.CanCancelLogin);
            Assert.False(signedOut.CanSignOut);

            var pending = CodexAccountUiState.From(new CodexAccountState
            {
                Status = CodexAccountStatus.LoginPending,
                ExecutablePath = @"C:\codex.exe",
                ServerConnected = true,
                CurrentLoginId = "login-1",
            });
            Assert.False(pending.CanSignIn);
            Assert.True(pending.CanCancelLogin);

            var signedInBusy = CodexAccountUiState.From(new CodexAccountState
            {
                Status = CodexAccountStatus.SignedIn,
                ExecutablePath = @"C:\codex.exe",
                ServerConnected = true,
                IsBusy = true,
            });
            Assert.False(signedInBusy.CanRefresh);
            Assert.False(signedInBusy.CanSignOut);
        }

        [Fact]
        public void PersistedCodexSettingsAndDisplayState_ContainNoAuthenticationMaterialFields()
        {
            var settings = new SmartHopperSettings
            {
                CodexExecutablePath = @"C:\Tools\codex.exe",
                CodexHome = @"C:\CodexProfile",
            };
            var state = new CodexAccountState
            {
                Status = CodexAccountStatus.SignedIn,
                Email = "person@example.com",
                PlanType = "plus",
                AuthenticationMode = "ChatGPT OAuth (Codex 관리)",
            };

            var serialized = (JsonConvert.SerializeObject(settings) + JsonConvert.SerializeObject(state)).ToLowerInvariant();
            Assert.DoesNotContain("accesstoken", serialized);
            Assert.DoesNotContain("refreshtoken", serialized);
            Assert.DoesNotContain("idtoken", serialized);
            Assert.DoesNotContain("authurl", serialized);
            Assert.DoesNotContain("clientid", serialized);
        }

        private sealed class FakeProbe : ICodexExecutableProbe
        {
            public string? LastPath { get; private set; }

            public Task<CodexExecutableProbeResult> ProbeAsync(string executablePath, CancellationToken cancellationToken = default)
            {
                this.LastPath = executablePath;
                return Task.FromResult(new CodexExecutableProbeResult
                {
                    Success = true,
                    Version = "codex-cli test",
                });
            }
        }
    }
}
