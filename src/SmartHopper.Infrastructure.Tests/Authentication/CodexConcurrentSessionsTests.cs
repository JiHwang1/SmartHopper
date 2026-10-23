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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.Core.Requests;
using SmartHopper.Infrastructure.AICall.Core.Returns;
using SmartHopper.Infrastructure.AICall.Sessions;
using SmartHopper.Infrastructure.Authentication.Codex;
using Xunit;

namespace SmartHopper.Infrastructure.Tests.Authentication
{
    public class CodexConcurrentSessionsTests
    {
        [Fact]
        public async Task TwoTurns_ResetObserverBeforeDeltasAndErrorsWithoutResettingHistory()
        {
            await using var transport = new SessionTransport();
            var observer = new Observer();
            using var session = CreateSession(transport, "owner", new Bridge("owner"), observer);

            await session.RunToStableResult(new SessionOptions());
            Assert.Empty(observer.Starts);

            session.AddInteraction("first question");
            var first = session.RunToStableResult(new SessionOptions());
            var firstTurn = await transport.WaitForTurnAsync("owner", 1);
            transport.Delta("owner", firstTurn, "first partial answer");
            transport.CompleteTurn("owner", firstTurn, "first answer");
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            await session.RunToStableResult(new SessionOptions());
            Assert.Single(observer.Starts);

            session.AddInteraction("second question");
            var second = session.RunToStableResult(new SessionOptions());
            var secondTurn = await transport.WaitForTurnAsync("owner", 2);
            transport.Delta("owner", secondTurn, "second partial answer");
            transport.CompleteTurn("owner", secondTurn, string.Empty, status: "failed");
            await Assert.ThrowsAsync<InvalidOperationException>(() => second.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal(2, observer.Starts.Count);
            Assert.All(observer.Starts, request => Assert.Same(session.Request, request));
            Assert.Equal(new[] { "start", "delta", "final", "start", "delta", "error" }, observer.Events);
            Assert.Equal(new[] { "second partial answer" }, Assert.Single(observer.ErrorDeltas));
            Assert.Equal(new[] { "first question", "first answer", "second question" },
                session.GetHistoryInteractionList().OfType<AIInteractionText>().Select(interaction => interaction.Content));

            await session.RunToStableResult(new SessionOptions());
            Assert.Equal(2, observer.Starts.Count);
        }

        [Fact]
        public async Task SharedTransport_RoutesConcurrentToolsAndRestoresPreviousHandlerOnLastDispose()
        {
            await using var transport = new SessionTransport();
            var legacyCalls = 0;
            CodexServerRequestHandler previous = (request, token) =>
            {
                legacyCalls++;
                return transport.SendResponseAsync(request.RequestId, new JValue("legacy"), token);
            };
            transport.ServerRequestHandler = previous;
            var firstBridge = new Bridge("first") { HoldExecution = true };
            using var first = CreateSession(transport, "first", firstBridge);
            var routerHandler = transport.ServerRequestHandler;
            using var second = CreateSession(transport, "second", new Bridge("second"));
            Assert.Same(routerHandler, transport.ServerRequestHandler);
            first.AddInteraction("first question");
            second.AddInteraction("second question");
            var firstTurn = first.RunToStableResult(new SessionOptions());
            var secondTurn = second.RunToStableResult(new SessionOptions());
            var firstId = await transport.WaitForTurnAsync("first", 1);
            var secondId = await transport.WaitForTurnAsync("second", 1);

            var firstTool = transport.RequestToolAsync("first-tool", "first", firstId);
            await firstBridge.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await transport.RequestToolAsync("second-tool", "second", secondId).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(firstTool.IsCompleted);
            var text = transport.Response("second-tool")["result"]!["contentItems"]![0]!.Value<string>("text")!;
            Assert.Equal("second", JObject.Parse(text).Value<string>("owner"));

            first.Dispose();
            await firstTool.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstTurn);
            Assert.Same(routerHandler, transport.ServerRequestHandler);
            await transport.RequestToolAsync("disposed-thread", "first", firstId);
            Assert.False(transport.Response("disposed-thread")["result"]!.Value<bool>("success"));
            Assert.Equal(1, firstBridge.Executions);
            Assert.Equal(0, legacyCalls);

            await transport.RequestToolAsync("second-still-live", "second", secondId, "next-call");
            transport.CompleteTurn("second", secondId, "second answer");
            await secondTurn.WaitAsync(TimeSpan.FromSeconds(2));
            second.Dispose();
            Assert.Same(previous, transport.ServerRequestHandler);
            await transport.Client.ProcessLineAsync("{\"id\":\"legacy\",\"method\":\"legacy/method\",\"params\":{}}");
            Assert.Equal(1, legacyCalls);
            Assert.All(transport.Responses.GroupBy(response => response["id"]!.ToString()), group => Assert.Single(group));
        }

        [Theory]
        [InlineData("unknown")]
        [InlineData(null)]
        public async Task UnknownOrMissingThread_DoesNotExecuteAnySession(string? thread)
        {
            await using var transport = new SessionTransport();
            var bridge = new Bridge("owner");
            using var session = CreateSession(transport, "owner", bridge);
            session.AddInteraction("question");
            var running = session.RunToStableResult(new SessionOptions());
            var turn = await transport.WaitForTurnAsync("owner", 1);
            await transport.RequestToolAsync("unknown", thread, turn);
            Assert.Equal(0, bridge.Executions);
            Assert.False(transport.Response("unknown")["result"]!.Value<bool>("success"));
            transport.CompleteTurn("owner", turn, "answer");
            await running.WaitAsync(TimeSpan.FromSeconds(2));
        }

        [Fact]
        public async Task EarlyAndStaleNotifications_UseTheTurnStartResponseIdentity()
        {
            await using var transport = new SessionTransport();
            var observer = new Observer();
            using var session = CreateSession(transport, "owner", new Bridge("owner"), observer);
            session.AddInteraction("first question");
            var first = session.RunToStableResult(new SessionOptions());
            var oldTurn = await transport.WaitForTurnAsync("owner", 1);
            transport.CompleteTurn("owner", oldTurn, "first answer");
            await first.WaitAsync(TimeSpan.FromSeconds(2));

            transport.BeforeTurnResponse = (thread, turn) =>
            {
                transport.Delta(thread, oldTurn, "stale");
                transport.CompleteTurn(thread, oldTurn, "wrong answer");
                transport.Delta(thread, turn, "current");
                transport.CompleteTurn(thread, turn, "second answer");
            };
            session.AddInteraction("second question");
            var result = await session.RunToStableResult(new SessionOptions()).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("second answer", result.Body.Interactions.OfType<AIInteractionText>().Last().Content);
            Assert.Equal(new[] { "current" }, observer.Deltas);
        }

        [Fact]
        public async Task ToolArrivingBeforeTurnStartContinuation_WaitsForAuthoritativeTurnId()
        {
            await using var transport = new SessionTransport();
            var bridge = new Bridge("owner");
            using var session = CreateSession(transport, "owner", bridge);
            Task? earlyTool = null;
            transport.BeforeTurnResponse = (thread, turn) => earlyTool = transport.RequestToolAsync("early", thread, turn);
            session.AddInteraction("question");
            var running = session.RunToStableResult(new SessionOptions());
            var turnId = await transport.WaitForTurnAsync("owner", 1);
            await earlyTool!.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, bridge.Executions);
            Assert.True(transport.Response("early")["result"]!.Value<bool>("success"));
            transport.CompleteTurn("owner", turnId, "answer");
            await running.WaitAsync(TimeSpan.FromSeconds(2));
        }

        [Fact]
        public async Task DisposalDuringThreadStart_DoesNotRegisterAnAbandonedSession()
        {
            await using var transport = new SessionTransport
            {
                ThreadStartWait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            using var session = CreateSession(transport, "owner", new Bridge("owner"));
            session.AddInteraction("question");
            var running = session.RunToStableResult(new SessionOptions());
            session.Dispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Null(transport.ServerRequestHandler);
            transport.ThreadStartWait.TrySetResult(true);
        }

        [Fact]
        public async Task DuplicateThreadId_DoesNotReplaceTheRegisteredSession()
        {
            await using var transport = new SessionTransport();
            var firstBridge = new Bridge("first");
            using var first = CreateSession(transport, "same-thread", firstBridge);
            using var duplicate = CreateSession(transport, "same-thread", new Bridge("duplicate"));
            first.AddInteraction("first question");
            var running = first.RunToStableResult(new SessionOptions());
            var turn = await transport.WaitForTurnAsync("same-thread", 1);
            duplicate.AddInteraction("duplicate question");
            await Assert.ThrowsAsync<ArgumentException>(() => duplicate.RunToStableResult(new SessionOptions()));
            duplicate.Dispose();
            await transport.RequestToolAsync("original-owner", "same-thread", turn);
            Assert.Equal(1, firstBridge.Executions);
            Assert.True(transport.Response("original-owner")["result"]!.Value<bool>("success"));
            transport.CompleteTurn("same-thread", turn, "answer");
            await running.WaitAsync(TimeSpan.FromSeconds(2));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CancelOneSession_CancelsItsToolAndInterruptsOnlyItsTurn(bool cancelCaller)
        {
            await using var transport = new SessionTransport();
            using var cancellation = new CancellationTokenSource();
            var firstBridge = new Bridge("first") { HoldExecution = true };
            using var first = CreateSession(transport, "first", firstBridge);
            using var second = CreateSession(transport, "second", new Bridge("second"));
            first.AddInteraction("first question");
            second.AddInteraction("second question");
            var firstTurn = first.RunToStableResult(new SessionOptions(), cancellation.Token);
            var secondTurn = second.RunToStableResult(new SessionOptions());
            var firstId = await transport.WaitForTurnAsync("first", 1);
            var secondId = await transport.WaitForTurnAsync("second", 1);
            var firstTool = transport.RequestToolAsync("cancelled", "first", firstId);
            await firstBridge.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (cancelCaller)
            {
                cancellation.Cancel();
            }
            else
            {
                first.Cancel();
            }

            await firstTool.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstTurn);
            Assert.False(transport.Response("cancelled")["result"]!.Value<bool>("success"));
            Assert.All(transport.Interrupts, interrupt => Assert.Equal("first", interrupt.Value<string>("threadId")));
            Assert.NotEmpty(transport.Interrupts);
            await transport.RequestToolAsync("unaffected", "second", secondId);
            Assert.True(transport.Response("unaffected")["result"]!.Value<bool>("success"));
            transport.CompleteTurn("second", secondId, "answer");
            await secondTurn.WaitAsync(TimeSpan.FromSeconds(2));
        }

        private static CodexConversationSession CreateSession(SessionTransport transport, string model, Bridge bridge, Observer? observer = null)
        {
            return new CodexConversationSession(transport, model, "system", observer ?? new Observer(), includeCurrentContext: false, dynamicToolBridge: bridge);
        }

        private sealed class SessionTransport : ICodexConversationTransport, IAsyncDisposable
        {
            private readonly StringWriter output = new ();
            private readonly ConcurrentDictionary<string, int> turnCounts = new ();
            private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> starts = new ();

            internal SessionTransport()
            {
                this.Client = new CodexJsonRpcClient(new StringReader(string.Empty), this.output);
            }

            public event EventHandler<CodexNotificationEventArgs>? NotificationReceived
            {
                add => this.Client.NotificationReceived += value;
                remove => this.Client.NotificationReceived -= value;
            }

            public CodexServerRequestHandler? ServerRequestHandler
            {
                get => this.Client.ServerRequestHandler;
                set => this.Client.ServerRequestHandler = value;
            }

            public bool SupportsDynamicTools => true;

            internal CodexJsonRpcClient Client { get; }

            internal Action<string, string>? BeforeTurnResponse { get; set; }

            internal TaskCompletionSource<bool>? ThreadStartWait { get; set; }

            internal ConcurrentQueue<JObject> Interrupts { get; } = new ();

            internal IEnumerable<JObject> Responses => this.output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(JObject.Parse);

            public Task EnsureSignedInAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public async Task<JToken> SendAsync(string method, JObject? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            {
                if (method == "thread/start")
                {
                    if (this.ThreadStartWait != null)
                    {
                        await this.ThreadStartWait.Task.WaitAsync(cancellationToken);
                    }

                    return new JObject { ["thread"] = new JObject { ["id"] = parameters!.Value<string>("model") } };
                }

                if (method == "turn/start")
                {
                    var thread = parameters!.Value<string>("threadId")!;
                    var count = this.turnCounts.AddOrUpdate(thread, 1, (_, previous) => previous + 1);
                    var turn = $"{thread}-turn-{count}";
                    this.BeforeTurnResponse?.Invoke(thread, turn);
                    this.Start(thread, count).TrySetResult(turn);
                    return new JObject { ["turn"] = new JObject { ["id"] = turn } };
                }

                if (method == "turn/interrupt")
                {
                    this.Interrupts.Enqueue((JObject)parameters!.DeepClone());
                    return new JObject();
                }

                throw new InvalidOperationException(method);
            }

            public Task SendResponseAsync(JToken requestId, JToken? result, CancellationToken cancellationToken = default) => this.Client.SendResponseAsync(requestId, result, cancellationToken);

            public Task SendErrorAsync(JToken requestId, int code, string message, CancellationToken cancellationToken = default) => this.Client.SendErrorAsync(requestId, code, message, cancellationToken);

            public ValueTask DisposeAsync() => this.Client.DisposeAsync();

            internal Task<string> WaitForTurnAsync(string thread, int count) => this.Start(thread, count).Task.WaitAsync(TimeSpan.FromSeconds(2));

            internal JObject Response(string requestId) => this.Responses.Single(response => response.Value<string>("id") == requestId);

            internal Task RequestToolAsync(string requestId, string? thread, string turn, string callId = "same-call")
            {
                return this.Client.ProcessLineAsync(new JObject
                {
                    ["id"] = requestId,
                    ["method"] = "item/tool/call",
                    ["params"] = new JObject
                    {
                        ["threadId"] = thread,
                        ["turnId"] = turn,
                        ["callId"] = callId,
                        ["namespace"] = "smarthopper",
                        ["tool"] = "gh_get_selected",
                        ["arguments"] = new JObject(),
                    },
                }.ToString());
            }

            internal void CompleteTurn(string thread, string turn, string text, string status = "completed")
            {
                this.Notify("turn/completed", new JObject
                {
                    ["threadId"] = thread,
                    ["turn"] = new JObject
                    {
                        ["id"] = turn,
                        ["status"] = status,
                        ["items"] = new JArray(new JObject { ["type"] = "agentMessage", ["text"] = text }),
                    },
                });
            }

            internal void Delta(string thread, string turn, string text)
            {
                this.Notify("item/agentMessage/delta", new JObject { ["threadId"] = thread, ["turnId"] = turn, ["delta"] = text });
            }

            private void Notify(string method, JObject parameters)
            {
                this.Client.ProcessLineAsync(new JObject { ["method"] = method, ["params"] = parameters }.ToString()).GetAwaiter().GetResult();
            }

            private TaskCompletionSource<string> Start(string thread, int count) => this.starts.GetOrAdd($"{thread}:{count}", _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        private sealed class Bridge : ICodexDynamicToolBridge
        {
            private readonly string owner;

            internal Bridge(string owner) => this.owner = owner;

            internal bool HoldExecution { get; set; }

            internal int Executions { get; private set; }

            internal TaskCompletionSource<bool> Entered { get; } = new (TaskCreationOptions.RunContinuationsAsynchronously);

            public JArray BuildDynamicTools() => new (new JObject { ["name"] = "smarthopper" });

            public async Task<CodexDynamicToolResult> ExecuteAsync(CodexDynamicToolCall call, CancellationToken cancellationToken)
            {
                this.Executions++;
                this.Entered.TrySetResult(true);
                if (this.HoldExecution)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return CodexDynamicToolResult.Ok(new JObject { ["owner"] = this.owner });
            }

            public void CancelPending() { }

            public void Dispose() { }
        }

        private sealed class Observer : IConversationObserver
        {
            internal List<string> Deltas { get; } = new ();

            internal List<AIRequestCall> Starts { get; } = new ();

            internal List<string> Events { get; } = new ();

            internal List<string[]> ErrorDeltas { get; } = new ();

            private readonly List<string> currentDeltas = new ();

            public void OnStart(AIRequestCall request)
            {
                this.Starts.Add(request);
                this.Events.Add("start");
                this.currentDeltas.Clear();
            }

            public void OnDelta(IAIInteraction interaction)
            {
                var content = ((AIInteractionText)interaction).Content;
                this.Deltas.Add(content);
                this.currentDeltas.Add(content);
                this.Events.Add("delta");
            }

            public void OnInteractionCompleted(IAIInteraction interaction) { }

            public void OnToolCall(AIInteractionToolCall toolCall) { }

            public void OnToolResult(AIInteractionToolResult toolResult) { }

            public void OnFinal(AIReturn finalResult) => this.Events.Add("final");

            public void OnError(Exception error)
            {
                this.Events.Add("error");
                this.ErrorDeltas.Add(this.currentDeltas.ToArray());
            }
        }
    }
}
