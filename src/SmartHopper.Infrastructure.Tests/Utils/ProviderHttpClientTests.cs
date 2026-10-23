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
using System.Collections.Generic;
#if NET7_WINDOWS
using System.Drawing;
#endif
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
#if NET7_WINDOWS
using Newtonsoft.Json.Linq;
using SmartHopper.Infrastructure.AICall.Core;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.Core.Requests;
using SmartHopper.Infrastructure.AIProviders;
#endif
using SmartHopper.Infrastructure.Utils;
using Xunit;

namespace SmartHopper.Infrastructure.Tests.Utils
{
    public class ProviderHttpClientTests
    {
        [Fact]
        public async Task DisposedClients_ReuseConnectionWithoutReplayingAuthenticationHeadersOrCookies()
        {
            await using var server = new LoopbackServer();
            Dictionary<string, string> first;
            using (var client = ProviderHttpClient.Create())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "first-key");
                client.Timeout = Timeout.InfiniteTimeSpan;
                HttpHeadersHelper.ApplyExtraHeaders(client, new Dictionary<string, string>
                {
                    ["X-Request-Only"] = "first",
                    ["Authorization"] = "ignored",
                });
                first = await ReadHeaders(client, server.Url);
                Assert.Equal("Bearer first-key", first["Authorization"]);
                Assert.Equal("first", first["X-Request-Only"]);
            }

            using (var client = ProviderHttpClient.Create())
            {
                client.Timeout = TimeSpan.FromSeconds(17);
                Assert.Equal(TimeSpan.FromSeconds(17), client.Timeout);
                client.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", "second-key");
                var second = await ReadHeaders(client, server.Url);
                Assert.Equal(first["Connection-Id"], second["Connection-Id"]);
                Assert.Equal("second-key", second["x-api-key"]);
                Assert.False(second.ContainsKey("Authorization"));
                Assert.False(second.ContainsKey("X-Request-Only"));
                Assert.False(second.ContainsKey("Cookie"));
            }

            using (var client = ProviderHttpClient.Create())
            {
                Assert.Equal(TimeSpan.FromSeconds(100), client.Timeout);
                var third = await ReadHeaders(client, server.Url);
                Assert.Equal(first["Connection-Id"], third["Connection-Id"]);
                Assert.False(third.ContainsKey("Authorization"));
                Assert.False(third.ContainsKey("x-api-key"));
                Assert.False(third.ContainsKey("X-Request-Only"));
                Assert.False(third.ContainsKey("Cookie"));
            }
        }

        [Fact]
        public async Task ConcurrentClients_SendOnlyTheirOwnAuthenticationAndHeaders()
        {
            await using var server = new LoopbackServer();
            await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
            {
                string authentication = index % 2 == 0 ? "bearer" : "x-api-key";
                using var client = ProviderHttpClient.Create();
                if (authentication == "bearer")
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"key-{index}");
                }
                else
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", $"key-{index}");
                }

                client.Timeout = TimeSpan.FromSeconds(20 + index);
                HttpHeadersHelper.ApplyExtraHeaders(client, new Dictionary<string, string>
                {
                    ["X-Request-Only"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });

                var headers = await ReadHeaders(client, server.Url);
                Assert.Equal(index.ToString(System.Globalization.CultureInfo.InvariantCulture), headers["X-Request-Only"]);
                Assert.Equal(TimeSpan.FromSeconds(20 + index), client.Timeout);
                if (authentication == "bearer")
                {
                    Assert.Equal($"Bearer key-{index}", headers["Authorization"]);
                    Assert.False(headers.ContainsKey("x-api-key"));
                }
                else
                {
                    Assert.Equal($"key-{index}", headers["x-api-key"]);
                    Assert.False(headers.ContainsKey("Authorization"));
                }
            }));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ClientCancellationOrTimeout_DoesNotAffectOtherClients(bool cancelExplicitly)
        {
            await using var server = new LoopbackServer();
            using var first = ProviderHttpClient.Create();
            using var second = ProviderHttpClient.Create();
            first.Timeout = cancelExplicitly ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(1);
            second.Timeout = TimeSpan.FromSeconds(10);
            var pending = first.GetAsync(new Uri(server.Url, "wait"));
            await server.WaitingRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancelExplicitly)
            {
                first.CancelPendingRequests();
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            });
            var headers = await ReadHeaders(second, server.Url);
            Assert.False(headers.ContainsKey("Cookie"));
            Assert.Equal(TimeSpan.FromSeconds(10), second.Timeout);
        }

#if NET7_WINDOWS
        [Fact]
        public async Task ProviderAndStreamingClients_SharePoolWithIndependentAuthenticationAndTimeouts()
        {
            await using var server = new LoopbackServer();
            var provider = new TestProvider();
            var adapter = new TestStreamingAdapter(provider);
            Dictionary<string, string> streamingHeaders;
            using (var streaming = adapter.CreateClient("bearer", "stream-key"))
            {
                Assert.Equal(TimeSpan.FromSeconds(100), streaming.Timeout);
                streaming.Timeout = Timeout.InfiniteTimeSpan;
                streamingHeaders = await ReadHeaders(streaming, server.Url);
                Assert.Equal("Bearer stream-key", streamingHeaders["Authorization"]);
            }

            using (var batch = provider.CreateBatchClient(17))
            {
                Assert.Equal(TimeSpan.FromSeconds(17), batch.Timeout);
                batch.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", "batch-key");
                var batchHeaders = await ReadHeaders(batch, server.Url);
                Assert.Equal(streamingHeaders["Connection-Id"], batchHeaders["Connection-Id"]);
                Assert.Equal("batch-key", batchHeaders["x-api-key"]);
                Assert.False(batchHeaders.ContainsKey("Authorization"));
                Assert.False(batchHeaders.ContainsKey("Cookie"));
            }

            using var anonymous = adapter.CreateClient("none", string.Empty);
            Assert.Equal(TimeSpan.FromSeconds(100), anonymous.Timeout);
            var anonymousHeaders = await ReadHeaders(anonymous, server.Url);
            Assert.Equal(streamingHeaders["Connection-Id"], anonymousHeaders["Connection-Id"]);
            Assert.False(anonymousHeaders.ContainsKey("Authorization"));
            Assert.False(anonymousHeaders.ContainsKey("x-api-key"));
            Assert.False(anonymousHeaders.ContainsKey("Cookie"));
        }

        [Theory]
        [InlineData(null, TimeoutDefaults.DefaultTimeoutSeconds)]
        [InlineData(-1, TimeoutDefaults.MinTimeoutSeconds)]
        [InlineData(0, TimeoutDefaults.MinTimeoutSeconds)]
        [InlineData(42, 42)]
        [InlineData(int.MaxValue, TimeoutDefaults.MaxTimeoutSeconds)]
        public void BatchClient_PreservesTimeoutDefaultsAndBounds(int? seconds, int expected)
        {
            var provider = new TestProvider();
            using var client = provider.CreateBatchClient(seconds);
            using var independent = provider.CreateBatchClient();
            Assert.Equal(TimeSpan.FromSeconds(expected), client.Timeout);
            Assert.Equal(TimeSpan.FromSeconds(TimeoutDefaults.DefaultTimeoutSeconds), independent.Timeout);
        }
#endif

        private static async Task<Dictionary<string, string>> ReadHeaders(HttpClient client, Uri url)
        {
            using var response = await client.GetAsync(url).WaitAsync(TimeSpan.FromSeconds(10));
            response.EnsureSuccessStatusCode();
            string body = await response.Content.ReadAsStringAsync();
            var headers = JsonConvert.DeserializeObject<Dictionary<string, string>>(body)!;
            return new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        }

#if NET7_WINDOWS
        private sealed class TestProvider : AIProvider
        {
            public override string Name => "HttpLifetimeTest";

            public override Image Icon => null!;

            public override bool IsEnabled => true;

            public override Uri DefaultServerUrl => new Uri("http://127.0.0.1/");

            public HttpClient CreateBatchClient(int? seconds = null) => this.CreateBatchHttpClient(seconds);

            public override string Encode(AIRequestCall request) => "{}";

            public override string Encode(IAIInteraction interaction) => "{}";

            public override List<IAIInteraction> Decode(JObject response) => new List<IAIInteraction>();
        }

        private sealed class TestStreamingAdapter : AIProviderStreamingAdapter
        {
            public TestStreamingAdapter(AIProvider provider)
                : base(provider)
            {
            }

            public HttpClient CreateClient(string authentication, string key)
            {
                var client = this.CreateHttpClient();
                this.ApplyAuthentication(client, authentication, key);
                return client;
            }
        }

#endif

        // A real HTTP/1.1 endpoint verifies socket reuse without external services or URL reservations.
        private sealed class LoopbackServer : IAsyncDisposable
        {
            private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource stopping = new CancellationTokenSource();
            private readonly ConcurrentBag<TcpClient> clients = new ConcurrentBag<TcpClient>();
            private readonly List<Task> connections = new List<Task>();
            private readonly Task accepting;

            public LoopbackServer()
            {
                this.listener.Start();
                this.Url = new Uri($"http://127.0.0.1:{((IPEndPoint)this.listener.LocalEndpoint).Port}/");
                this.accepting = this.AcceptAsync();
            }

            public Uri Url { get; }

            public TaskCompletionSource<bool> WaitingRequest { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public async ValueTask DisposeAsync()
            {
                this.stopping.Cancel();
                this.listener.Stop();
                await this.accepting;
                foreach (var client in this.clients)
                {
                    client.Dispose();
                }

                await Task.WhenAll(this.connections);
                this.stopping.Dispose();
            }

            private async Task AcceptAsync()
            {
                try
                {
                    while (true)
                    {
                        var client = await this.listener.AcceptTcpClientAsync(this.stopping.Token);
                        this.clients.Add(client);
                        this.connections.Add(this.ServeAsync(client, this.connections.Count));
                    }
                }
                catch (Exception) when (this.stopping.IsCancellationRequested)
                {
                }
            }

            private async Task ServeAsync(TcpClient client, int connectionId)
            {
                try
                {
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    while (await reader.ReadLineAsync().WaitAsync(this.stopping.Token) is string requestLine)
                    {
                        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Connection-Id"] = connectionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        };
                        while (await reader.ReadLineAsync().WaitAsync(this.stopping.Token) is string line && line.Length > 0)
                        {
                            int colon = line.IndexOf(':');
                            headers.Add(line.Substring(0, colon), line.Substring(colon + 1).Trim());
                        }

                        if (requestLine.StartsWith("GET /wait ", StringComparison.Ordinal))
                        {
                            this.WaitingRequest.TrySetResult(true);
                            await Task.Delay(Timeout.InfiniteTimeSpan, this.stopping.Token);
                        }

                        byte[] body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(headers));
                        byte[] responseHeaders = Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nSet-Cookie: provider-session=private; Path=/\r\n\r\n");
                        await stream.WriteAsync(responseHeaders, this.stopping.Token);
                        await stream.WriteAsync(body, this.stopping.Token);
                    }
                }
                catch (Exception) when (this.stopping.IsCancellationRequested)
                {
                }
            }
        }
    }
}
