/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 * Licensed under the GNU Lesser General Public License.
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
    }
}
