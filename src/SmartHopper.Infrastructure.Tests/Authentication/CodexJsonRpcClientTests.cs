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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SmartHopper.Infrastructure.Authentication.Codex;
using Xunit;

namespace SmartHopper.Infrastructure.Tests.Authentication
{
    public class CodexJsonRpcClientTests
    {
        [Fact]
        public async Task Response_CompletesMatchingPendingRequest()
        {
            using var output = new StringWriter();
            await using var client = new CodexJsonRpcClient(new StringReader(string.Empty), output);
            var pending = client.SendRequestAsync("account/read", timeout: TimeSpan.FromSeconds(1));
            await WaitForLineCountAsync(output, 1);

            await client.ProcessLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"ok\":true}}");

            Assert.True((bool)(await pending)["ok"]!);
        }

        [Fact]
        public async Task ReorderedResponses_RemainMatchedByRequestId()
        {
            using var output = new StringWriter();
            await using var client = new CodexJsonRpcClient(new StringReader(string.Empty), output);
            var first = client.SendRequestAsync("first", timeout: TimeSpan.FromSeconds(1));
            var second = client.SendRequestAsync("second", timeout: TimeSpan.FromSeconds(1));
            await WaitForLineCountAsync(output, 2);

            await client.ProcessLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":\"second\"}");
            await client.ProcessLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"first\"}");

            Assert.Equal("first", (string?)await first);
            Assert.Equal("second", (string?)await second);
        }

        [Fact]
        public async Task Notification_IsNotTreatedAsResponse()
        {
            using var output = new StringWriter();
            await using var client = new CodexJsonRpcClient(new StringReader(string.Empty), output);
            var notifications = 0;
            client.NotificationReceived += (_, e) =>
            {
                Assert.Equal("account/updated", e.Method);
                notifications++;
            };
            var pending = client.SendRequestAsync("account/read", timeout: TimeSpan.FromSeconds(1));
            await WaitForLineCountAsync(output, 1);

            await client.ProcessLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"account/updated\",\"params\":{}}");

            Assert.Equal(1, notifications);
            Assert.False(pending.IsCompleted);
            await client.ProcessLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}");
            await pending;
        }

        [Fact]
        public async Task MalformedLine_DoesNotStopLaterMessageProcessing()
        {
            using var output = new StringWriter();
            await using var client = new CodexJsonRpcClient(new StringReader(string.Empty), output);
            var malformed = 0;
            client.MalformedMessageReceived += (_, _) => malformed++;
            var pending = client.SendRequestAsync("account/read", timeout: TimeSpan.FromSeconds(1));
            await WaitForLineCountAsync(output, 1);

            await client.ProcessLineAsync("not-json");
            await client.ProcessLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"continued\":true}}");

            Assert.Equal(1, malformed);
            Assert.True((bool)(await pending)["continued"]!);
        }

        [Fact]
        public async Task Request_RespectsTimeoutAndCancellation()
        {
            using var timeoutOutput = new StringWriter();
            await using var timeoutClient = new CodexJsonRpcClient(new StringReader(string.Empty), timeoutOutput);
            await Assert.ThrowsAsync<TimeoutException>(() => timeoutClient.SendRequestAsync(
                "timeout",
                timeout: TimeSpan.FromMilliseconds(30)));

            using var cancellationOutput = new StringWriter();
            await using var cancellationClient = new CodexJsonRpcClient(new StringReader(string.Empty), cancellationOutput);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellationClient.SendRequestAsync(
                "cancel",
                timeout: TimeSpan.FromSeconds(5),
                cancellationToken: cancellation.Token));
        }

        [Fact]
        public async Task TransportCompletion_FailsPendingRequests()
        {
            using var output = new StringWriter();
            await using var client = new CodexJsonRpcClient(new StringReader(string.Empty), output);
            var pending = client.SendRequestAsync("account/read", timeout: TimeSpan.FromSeconds(5));
            await WaitForLineCountAsync(output, 1);

            client.Complete();

            await Assert.ThrowsAsync<CodexTransportClosedException>(() => pending);
        }

        [Fact]
        public async Task ServerRequest_PreservesStringIdAndSendsOneResponse()
        {
            using var output = new StringWriter();
            await using var client = new CodexJsonRpcClient(new StringReader(string.Empty), output);
            client.ServerRequestHandler = async (request, cancellationToken) =>
            {
                Assert.Equal(JTokenType.String, request.RequestId.Type);
                Assert.Equal("server-1", request.RequestId.Value<string>());
                Assert.Equal("item/tool/call", request.Method);
                Assert.Equal("gh_get", request.Parameters.Value<string>("tool"));
                await client.SendResponseAsync(
                    request.RequestId,
                    new JObject { ["success"] = true },
                    cancellationToken);
            };

            await client.ProcessLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":\"server-1\",\"method\":\"item/tool/call\",\"params\":{\"tool\":\"gh_get\"}}");

            var response = JObject.Parse(GetLines(output).Single());
            Assert.Equal(JTokenType.String, response["id"]!.Type);
            Assert.Equal("server-1", response.Value<string>("id"));
            Assert.True(response["result"]!.Value<bool>("success"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendResponseAsync(
                new JValue("server-1"),
                new JObject()));
        }

        [Fact]
        public async Task ServerRequest_HandlerCanSendError()
        {
            using var output = new StringWriter();
            await using var client = new CodexJsonRpcClient(new StringReader(string.Empty), output);
            client.ServerRequestHandler = (request, cancellationToken) => client.SendErrorAsync(
                request.RequestId,
                -32001,
                "요청을 실행할 수 없습니다.",
                cancellationToken);

            await client.ProcessLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"item/tool/call\",\"params\":{}}");

            var response = JObject.Parse(GetLines(output).Single());
            Assert.Equal(JTokenType.Integer, response["id"]!.Type);
            Assert.Equal(42, response.Value<int>("id"));
            Assert.Equal(-32001, response["error"]!.Value<int>("code"));
        }

        [Fact]
        public async Task ServerRequest_WithoutHandlerReturnsMethodNotFound()
        {
            using var output = new StringWriter();
            await using var client = new CodexJsonRpcClient(new StringReader(string.Empty), output);

            await client.ProcessLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"unknown/method\",\"params\":{}}");

            var response = JObject.Parse(GetLines(output).Single());
            Assert.Equal(-32601, response["error"]!.Value<int>("code"));
        }

        [Fact]
        public async Task ServerRequest_HandlerMustRespondExactlyOnce()
        {
            using var output = new StringWriter();
            await using var client = new CodexJsonRpcClient(new StringReader(string.Empty), output);
            client.ServerRequestHandler = (_, _) => Task.CompletedTask;

            await client.ProcessLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"item/tool/call\",\"params\":{}}");

            var response = JObject.Parse(GetLines(output).Single());
            Assert.Equal(-32603, response["error"]!.Value<int>("code"));
            Assert.Contains("did not send a response", response["error"]!.Value<string>("message"));
        }

        [Fact]
        public async Task DuplicateServerRequestId_IsRejectedWithoutReplacingOriginalRequest()
        {
            using var output = new StringWriter();
            await using var client = new CodexJsonRpcClient(new StringReader(string.Empty), output);
            var handlerStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseHandler = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ServerRequestHandler = async (request, cancellationToken) =>
            {
                handlerStarted.TrySetResult(true);
                await releaseHandler.Task.WaitAsync(cancellationToken);
                await client.SendResponseAsync(request.RequestId, new JObject { ["ok"] = true }, cancellationToken);
            };

            var original = client.ProcessLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":\"same\",\"method\":\"item/tool/call\",\"params\":{}}");
            await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await client.ProcessLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":\"same\",\"method\":\"item/tool/call\",\"params\":{}}");
            releaseHandler.TrySetResult(true);
            await original;

            var responses = GetLines(output).Select(JObject.Parse).ToList();
            Assert.Equal(2, responses.Count);
            Assert.Contains(responses, response => response["error"]?.Value<int>("code") == -32600);
            Assert.Contains(responses, response => response["result"]?.Value<bool>("ok") == true);
        }

        [Fact]
        public async Task MalformedServerRequest_ReturnsLimitedErrorAndDoesNotExposeArguments()
        {
            using var output = new StringWriter();
            await using var client = new CodexJsonRpcClient(new StringReader(string.Empty), output);
            var malformed = 0;
            client.MalformedMessageReceived += (_, _) => malformed++;

            await client.ProcessLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"item/tool/call\",\"params\":[\"oauth-secret-value\"]}");

            Assert.Equal(1, malformed);
            Assert.DoesNotContain("oauth-secret-value", output.ToString());
            var response = JObject.Parse(GetLines(output).Single());
            Assert.Equal(-32602, response["error"]!.Value<int>("code"));
        }

        [Fact]
        public async Task TransportCompletion_CancelsActiveServerRequest()
        {
            using var output = new StringWriter();
            await using var client = new CodexJsonRpcClient(new StringReader(string.Empty), output);
            var handlerStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationObserved = false;
            client.ServerRequestHandler = async (_, cancellationToken) =>
            {
                handlerStarted.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved = true;
                    throw;
                }
            };

            var processing = client.ProcessLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"item/tool/call\",\"params\":{}}");
            await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            client.Complete();
            await processing;

            Assert.True(cancellationObserved);
            Assert.Empty(GetLines(output));
        }

        [Fact]
        public async Task LateResponse_IsIgnored()
        {
            using var output = new StringWriter();
            await using var client = new CodexJsonRpcClient(new StringReader(string.Empty), output);

            await client.ProcessLineAsync("{\"jsonrpc\":\"2.0\",\"id\":999,\"result\":{\"late\":true}}");

            Assert.Empty(GetLines(output));
        }

        private static async Task WaitForLineCountAsync(StringWriter writer, int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                var lines = writer.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length >= count)
                {
                    return;
                }

                await Task.Delay(5);
            }

            throw new TimeoutException("JSON-RPC request was not written in time.");
        }

        private static string[] GetLines(StringWriter writer)
        {
            return writer.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
