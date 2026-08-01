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
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SmartHopper.Infrastructure.Authentication.Codex
{
    /// <summary>
    /// User-visible state of the Codex account integration.
    /// </summary>
    public enum CodexAccountStatus
    {
        NotInitialized,
        CodexNotFound,
        StartingServer,
        SignedOut,
        LoginPending,
        SignedIn,
        Error,
    }

    /// <summary>
    /// Display-only account and connection state. Authentication material is deliberately absent.
    /// </summary>
    public sealed class CodexAccountState
    {
        public CodexAccountStatus Status { get; init; } = CodexAccountStatus.NotInitialized;

        public string? ExecutablePath { get; init; }

        public string? CodexVersion { get; init; }

        public bool ServerConnected { get; init; }

        public bool IsBusy { get; init; }

        public string? Email { get; init; }

        public string? PlanType { get; init; }

        public string? AuthenticationMode { get; init; }

        public CodexRateLimitWindow? PrimaryRateLimit { get; init; }

        public CodexRateLimitWindow? SecondaryRateLimit { get; init; }

        public string? CurrentLoginId { get; init; }

        public string? UserMessage { get; init; }

        public DateTimeOffset? LastRefreshedAt { get; init; }
    }

    /// <summary>
    /// Display-only rolling usage window returned by account/rateLimits/read.
    /// </summary>
    public sealed class CodexRateLimitWindow
    {
        public int UsedPercent { get; init; }

        public int RemainingPercent => Math.Max(0, 100 - this.UsedPercent);

        public long? WindowDurationMinutes { get; init; }

        public DateTimeOffset? ResetsAt { get; init; }
    }

    /// <summary>
    /// Event payload for account state changes.
    /// </summary>
    public sealed class CodexAccountStateChangedEventArgs : EventArgs
    {
        public CodexAccountStateChangedEventArgs(CodexAccountState state)
        {
            this.State = state ?? throw new ArgumentNullException(nameof(state));
        }

        public CodexAccountState State { get; }
    }

    /// <summary>
    /// Result of locating and probing a Codex executable.
    /// </summary>
    public sealed class CodexExecutableInfo
    {
        public bool IsAvailable { get; init; }

        public string? Path { get; init; }

        public string? Version { get; init; }

        public string? UserMessage { get; init; }
    }

    /// <summary>
    /// Launch settings for one Codex app-server instance.
    /// </summary>
    public sealed class CodexLaunchOptions
    {
        public string ExecutablePath { get; init; } = string.Empty;

        public string? CodexHome { get; init; }
    }

    /// <summary>
    /// Login result that intentionally excludes the authentication URL.
    /// </summary>
    public sealed class CodexLoginStartResult
    {
        public bool BrowserOpened { get; init; }

        public string? LoginId { get; init; }

        public string? UserMessage { get; init; }
    }

    /// <summary>
    /// Central account operations supported by the Codex Account settings tab.
    /// </summary>
    public interface ICodexAccountService
    {
        event EventHandler<CodexAccountStateChangedEventArgs>? StateChanged;

        CodexAccountState CurrentState { get; }

        Task ConfigureAsync(string? executablePath, string? codexHome, CancellationToken cancellationToken = default);

        Task ConnectAsync(CancellationToken cancellationToken = default);

        Task<CodexAccountState> RefreshAsync(bool refreshToken = false, CancellationToken cancellationToken = default);

        Task<CodexAccountState> RefreshRateLimitsAsync(CancellationToken cancellationToken = default);

        Task<CodexLoginStartResult> StartChatGptLoginAsync(CancellationToken cancellationToken = default);

        Task CancelLoginAsync(CancellationToken cancellationToken = default);

        Task LogoutAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Shared initialized app-server transport used by Codex-backed conversations.
    /// </summary>
    public interface ICodexConversationTransport
    {
        event EventHandler<CodexNotificationEventArgs>? NotificationReceived;

        CodexServerRequestHandler? ServerRequestHandler { get; set; }

        bool SupportsDynamicTools { get; }

        Task EnsureSignedInAsync(CancellationToken cancellationToken = default);

        Task<JToken> SendAsync(
            string method,
            JObject? parameters = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default);

        Task SendResponseAsync(JToken requestId, JToken? result, CancellationToken cancellationToken = default);

        Task SendErrorAsync(JToken requestId, int code, string message, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Codex 모델이 지원한다고 알린 추론 수준입니다.
    /// </summary>
    public sealed class CodexReasoningEffortOption
    {
        public string Value { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;
    }

    /// <summary>
    /// Codex app-server 모델 목록이 반환한 모델입니다.
    /// </summary>
    public sealed class CodexModelOption
    {
        public string Model { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;

        public bool IsDefault { get; init; }

        public string DefaultReasoningEffort { get; init; } = string.Empty;

        public IReadOnlyList<CodexReasoningEffortOption> SupportedReasoningEfforts { get; init; }
            = Array.Empty<CodexReasoningEffortOption>();
    }

    /// <summary>
    /// Codex 기반 대화가 선택적으로 제공하는 실행 설정 화면 계약입니다.
    /// </summary>
    public interface ICodexConfigurableConversationSession
    {
        string SelectedModel { get; }

        string SelectedReasoningEffort { get; }

        Task<IReadOnlyList<CodexModelOption>> GetModelsAsync(CancellationToken cancellationToken = default);

        void Configure(string? model, string? reasoningEffort);
    }

    /// <summary>
    /// Locates and probes a Codex executable without invoking a command shell.
    /// </summary>
    public interface ICodexExecutableLocator
    {
        Task<CodexExecutableInfo> LocateAsync(string? configuredPath, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Opens a validated URI in the system browser.
    /// </summary>
    public interface ICodexBrowserLauncher
    {
        void Open(Uri uri);
    }

    /// <summary>
    /// Notification emitted by the app-server JSON-RPC transport.
    /// </summary>
    public sealed class CodexNotificationEventArgs : EventArgs
    {
        public CodexNotificationEventArgs(string method, JObject parameters)
        {
            this.Method = method ?? throw new ArgumentNullException(nameof(method));
            this.Parameters = parameters ?? new JObject();
        }

        public string Method { get; }

        public JObject Parameters { get; }
    }

    /// <summary>
    /// 서버가 시작한 JSON-RPC 요청입니다. 응답에서 정확히 되돌려줄 수 있도록 request ID의
    /// 원래 JSON 문자열 또는 정수 표현을 보존합니다.
    /// </summary>
    public sealed class CodexServerRequest
    {
        public CodexServerRequest(JToken requestId, string method, JObject parameters)
        {
            this.RequestId = requestId?.DeepClone() ?? throw new ArgumentNullException(nameof(requestId));
            this.Method = method ?? throw new ArgumentNullException(nameof(method));
            this.Parameters = parameters != null
                ? (JObject)parameters.DeepClone()
                : new JObject();
        }

        public JToken RequestId { get; }

        public string Method { get; }

        public JObject Parameters { get; }
    }

    /// <summary>
    /// 서버가 시작한 JSON-RPC 요청 하나를 처리하는 비동기 callback입니다.
    /// callback은 transport를 통해 response 또는 error 하나를 보내고 끝나야 합니다.
    /// </summary>
    public delegate Task CodexServerRequestHandler(CodexServerRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Minimal connection abstraction used by the account service and test fakes.
    /// </summary>
    public interface ICodexAppServerHost : IAsyncDisposable
    {
        event EventHandler<CodexNotificationEventArgs>? NotificationReceived;

        CodexServerRequestHandler? ServerRequestHandler { get; set; }

        bool IsRunning { get; }

        bool SupportsDynamicTools { get; }

        Task StartAsync(CodexLaunchOptions options, CancellationToken cancellationToken = default);

        Task<JToken> SendRequestAsync(string method, JObject? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

        Task SendResponseAsync(JToken requestId, JToken? result, CancellationToken cancellationToken = default);

        Task SendErrorAsync(JToken requestId, int code, string message, CancellationToken cancellationToken = default);

        Task StopAsync(CancellationToken cancellationToken = default);

        void BeginShutdown();

        void Abort();
    }

    /// <summary>
    /// A JSON-RPC error returned by Codex. The raw error data is not retained.
    /// </summary>
    public sealed class CodexJsonRpcException : Exception
    {
        public CodexJsonRpcException(int code)
            : base($"Codex JSON-RPC error {code}.")
        {
            this.Code = code;
        }

        public int Code { get; }
    }

    /// <summary>
    /// Raised when app-server transport ends while requests are pending.
    /// </summary>
    public sealed class CodexTransportClosedException : Exception
    {
        public CodexTransportClosedException()
            : base("Codex app-server connection closed.")
        {
        }
    }

    /// <summary>
    /// Pure UI policy derived from display-only state.
    /// </summary>
    public sealed class CodexAccountUiState
    {
        public bool CanConnect { get; init; }

        public bool CanSignIn { get; init; }

        public bool CanCancelLogin { get; init; }

        public bool CanSignOut { get; init; }

        public bool CanRefresh { get; init; }

        public static CodexAccountUiState From(CodexAccountState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var executableAvailable = !string.IsNullOrWhiteSpace(state.ExecutablePath);
            var loginPending = !string.IsNullOrWhiteSpace(state.CurrentLoginId);
            return new CodexAccountUiState
            {
                CanConnect = executableAvailable && !state.IsBusy && !state.ServerConnected,
                CanSignIn = executableAvailable && !state.IsBusy && !loginPending && state.Status != CodexAccountStatus.SignedIn,
                CanCancelLogin = !state.IsBusy && loginPending,
                CanSignOut = !state.IsBusy && state.Status == CodexAccountStatus.SignedIn,
                CanRefresh = executableAvailable && !state.IsBusy,
            };
        }
    }
}
