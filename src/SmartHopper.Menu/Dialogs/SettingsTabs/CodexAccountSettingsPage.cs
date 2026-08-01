/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public License.
 */

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using SmartHopper.Infrastructure.Authentication.Codex;
using SmartHopper.Infrastructure.Settings;

namespace SmartHopper.Menu.Dialogs.SettingsTabs
{
    /// <summary>
    /// Account-only Codex app-server settings page, independent from AI provider pages.
    /// </summary>
    public sealed class CodexAccountSettingsPage : Panel
    {
        private readonly ICodexAccountService service;
        private readonly TextBox executablePath = new ();
        private readonly TextBox codexHome = new ();
        private readonly Label discoveryStatus = new ();
        private readonly Label version = new ();
        private readonly Label serverStatus = new ();
        private readonly Label loginStatus = new ();
        private readonly Label email = new ();
        private readonly Label plan = new ();
        private readonly Label authenticationMode = new ();
        private readonly Label primaryLimit = new ();
        private readonly Label primaryReset = new ();
        private readonly Label secondaryLimit = new ();
        private readonly Label secondaryReset = new ();
        private readonly Label refreshedAt = new ();
        private readonly Label message = new () { Wrap = WrapMode.Word };
        private readonly Button connectButton = new () { Text = "app-server 시작 및 연결" };
        private readonly Button signInButton = new () { Text = "Sign in with ChatGPT" };
        private readonly Button cancelLoginButton = new () { Text = "Cancel login" };
        private readonly Button signOutButton = new () { Text = "Sign out" };
        private readonly Button refreshButton = new () { Text = "Refresh" };
        private readonly CancellationTokenSource lifetime = new ();
        private bool localBusy;
        private bool disposed;

        public CodexAccountSettingsPage(ICodexAccountService service, SmartHopperSettings settings)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            this.executablePath.Text = settings.CodexExecutablePath ?? string.Empty;
            this.codexHome.Text = settings.CodexHome ?? string.Empty;
            this.BuildLayout();
            this.WireEvents();
            this.service.StateChanged += this.OnStateChanged;
            this.UpdateState(this.service.CurrentState);
            _ = this.InitializeAsync();
        }

        public void SaveSettings(SmartHopperSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            settings.CodexExecutablePath = NormalizeOptional(this.executablePath.Text);
            settings.CodexHome = NormalizeOptional(this.codexHome.Text);
        }

        public void ReleaseSubscriptions()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.service.StateChanged -= this.OnStateChanged;
            this.lifetime.Cancel();
            this.lifetime.Dispose();
        }

        private static string? NormalizeOptional(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string ToStatusText(CodexAccountStatus status)
        {
            return status switch
            {
                CodexAccountStatus.NotInitialized => "초기화되지 않음",
                CodexAccountStatus.CodexNotFound => "Codex CLI를 찾지 못함",
                CodexAccountStatus.StartingServer => "app-server 시작 중",
                CodexAccountStatus.SignedOut => "로그아웃됨",
                CodexAccountStatus.LoginPending => "브라우저 로그인 진행 중",
                CodexAccountStatus.SignedIn => "로그인됨",
                CodexAccountStatus.Error => "오류",
                _ => "알 수 없음",
            };
        }

        private void BuildLayout()
        {
            var browseExecutable = new Button { Text = "찾아보기" };
            var useAutomatic = new Button { Text = "자동 탐색" };
            var browseHome = new Button { Text = "찾아보기" };

            browseExecutable.Click += (_, _) =>
            {
                using var dialog = new OpenFileDialog
                {
                    Title = "Codex CLI 실행 파일 선택",
                    MultiSelect = false,
                };
                if (dialog.ShowDialog(this.ParentWindow) == DialogResult.Ok)
                {
                    this.executablePath.Text = dialog.FileName;
                }
            };
            useAutomatic.Click += (_, _) => this.executablePath.Text = string.Empty;
            browseHome.Click += (_, _) =>
            {
                using var dialog = new SelectFolderDialog { Title = "선택적 CODEX_HOME 폴더 선택" };
                if (dialog.ShowDialog(this.ParentWindow) == DialogResult.Ok)
                {
                    this.codexHome.Text = dialog.Directory;
                }
            };

            var pathActions = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Items = { this.executablePath, browseExecutable, useAutomatic },
            };
            var homeActions = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Items = { this.codexHome, browseHome },
            };

            var form = new TableLayout
            {
                Spacing = new Size(8, 6),
                Rows =
                {
                    Row("Codex CLI 경로", pathActions),
                    Row("선택적 CODEX_HOME", homeActions),
                    Row("탐색 상태", this.discoveryStatus),
                    Row("Codex 버전", this.version),
                    Row("app-server", this.serverStatus),
                    Row("로그인 상태", this.loginStatus),
                    Row("계정 이메일", this.email),
                    Row("ChatGPT 플랜", this.plan),
                    Row("인증 방식", this.authenticationMode),
                    Row("단기 잔여 한도", this.primaryLimit),
                    Row("단기 초기화", this.primaryReset),
                    Row("장기 잔여 한도", this.secondaryLimit),
                    Row("장기 초기화", this.secondaryReset),
                    Row("마지막 새로고침", this.refreshedAt),
                },
            };

            var actions = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Items =
                {
                    this.connectButton,
                    this.signInButton,
                    this.cancelLoginButton,
                    this.signOutButton,
                    this.refreshButton,
                },
            };

            var layout = new DynamicLayout
            {
                Padding = new Padding(10),
                Spacing = new Size(5, 8),
            };
            layout.Add(new Label
            {
                Text = "Codex app-server가 ChatGPT OAuth 로그인과 인증 정보의 저장·갱신을 담당합니다. SmartHopper는 OAuth 토큰을 읽거나 저장하지 않습니다.",
                Wrap = WrapMode.Word,
                TextColor = Colors.Gray,
            });
            layout.Add(form);
            layout.Add(actions);
            layout.Add(this.message);
            layout.Add(new Label
            {
                Text = "Canvas Assistant에서 'Codex Account'를 선택하면 이 ChatGPT 플랜을 사용합니다.",
                Wrap = WrapMode.Word,
                TextColor = Colors.Gray,
            });
            this.Content = new Scrollable { Content = layout };
        }

        private static TableRow Row(string title, Control control)
        {
            return new TableRow(
                new TableCell(new Label { Text = title + ":", Width = 145, VerticalAlignment = VerticalAlignment.Center }),
                new TableCell(control, scaleWidth: true));
        }

        private void WireEvents()
        {
            this.connectButton.Click += async (_, _) => await this.RunActionAsync(
                token => this.service.ConnectAsync(token)).ConfigureAwait(false);
            this.signInButton.Click += async (_, _) => await this.RunActionAsync(
                async token => { await this.service.StartChatGptLoginAsync(token).ConfigureAwait(false); }).ConfigureAwait(false);
            this.cancelLoginButton.Click += async (_, _) => await this.RunActionAsync(
                token => this.service.CancelLoginAsync(token)).ConfigureAwait(false);
            this.signOutButton.Click += async (_, _) => await this.RunActionAsync(
                token => this.service.LogoutAsync(token)).ConfigureAwait(false);
            this.refreshButton.Click += async (_, _) => await this.RunActionAsync(
                async token => { await this.service.RefreshAsync(refreshToken: false, token).ConfigureAwait(false); }).ConfigureAwait(false);
        }

        private async Task InitializeAsync()
        {
            try
            {
                this.localBusy = true;
                this.MarshalState(this.service.CurrentState);
                await this.service.ConfigureAsync(
                    NormalizeOptional(this.executablePath.Text),
                    NormalizeOptional(this.codexHome.Text),
                    this.lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (this.lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                this.localBusy = false;
                this.MarshalState(this.service.CurrentState);
            }
        }

        private async Task RunActionAsync(Func<CancellationToken, Task> action)
        {
            if (this.localBusy || this.disposed)
            {
                return;
            }

            this.localBusy = true;
            this.MarshalState(this.service.CurrentState);
            try
            {
                await this.service.ConfigureAsync(
                    NormalizeOptional(this.executablePath.Text),
                    NormalizeOptional(this.codexHome.Text),
                    this.lifetime.Token).ConfigureAwait(false);
                await action(this.lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (this.lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                this.localBusy = false;
                this.MarshalState(this.service.CurrentState);
            }
        }

        private void OnStateChanged(object? sender, CodexAccountStateChangedEventArgs e)
        {
            this.MarshalState(e.State);
        }

        private void MarshalState(CodexAccountState state)
        {
            if (this.disposed)
            {
                return;
            }

            RhinoApp.InvokeOnUiThread(() =>
            {
                if (!this.disposed)
                {
                    this.UpdateState(state);
                }
            });
        }

        private void UpdateState(CodexAccountState state)
        {
            this.discoveryStatus.Text = state.Status == CodexAccountStatus.CodexNotFound
                ? "찾지 못함"
                : string.IsNullOrWhiteSpace(state.ExecutablePath) ? "확인 전" : state.ExecutablePath;
            this.version.Text = state.CodexVersion ?? "-";
            this.serverStatus.Text = state.ServerConnected ? "연결됨" : "연결되지 않음";
            this.loginStatus.Text = ToStatusText(state.Status);
            this.email.Text = state.Email ?? "-";
            this.plan.Text = state.PlanType ?? "-";
            this.authenticationMode.Text = state.AuthenticationMode ?? "-";
            this.primaryLimit.Text = FormatLimit(state.PrimaryRateLimit);
            this.primaryReset.Text = FormatReset(state.PrimaryRateLimit);
            this.secondaryLimit.Text = FormatLimit(state.SecondaryRateLimit);
            this.secondaryReset.Text = FormatReset(state.SecondaryRateLimit);
            this.refreshedAt.Text = state.LastRefreshedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) ?? "-";
            this.message.Text = state.UserMessage ?? string.Empty;
            this.message.TextColor = state.Status == CodexAccountStatus.Error || state.Status == CodexAccountStatus.CodexNotFound
                ? Colors.DarkRed
                : Colors.Gray;

            var policy = CodexAccountUiState.From(state);
            var enabled = !this.localBusy;
            this.connectButton.Enabled = enabled && policy.CanConnect;
            this.signInButton.Enabled = enabled && policy.CanSignIn;
            this.cancelLoginButton.Enabled = enabled && policy.CanCancelLogin;
            this.signOutButton.Enabled = enabled && policy.CanSignOut;
            this.refreshButton.Enabled = enabled && policy.CanRefresh;
        }

        private static string FormatLimit(CodexRateLimitWindow? window)
        {
            if (window == null)
            {
                return "-";
            }

            var duration = window.WindowDurationMinutes.HasValue
                ? $" ({FormatDuration(window.WindowDurationMinutes.Value)} 기준)"
                : string.Empty;
            return $"{window.RemainingPercent}% 남음{duration}";
        }

        private static string FormatReset(CodexRateLimitWindow? window)
        {
            return window?.ResetsAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) ?? "-";
        }

        private static string FormatDuration(long minutes)
        {
            if (minutes >= 1440 && minutes % 1440 == 0)
            {
                return $"{minutes / 1440}일";
            }

            if (minutes >= 60 && minutes % 60 == 0)
            {
                return $"{minutes / 60}시간";
            }

            return $"{minutes}분";
        }
    }
}
