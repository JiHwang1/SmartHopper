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
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SmartHopper.Infrastructure.AICall.Core.Base;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.Core.Requests;
using SmartHopper.Infrastructure.AICall.Core.Returns;
using SmartHopper.Infrastructure.AICall.Execution;
using SmartHopper.Infrastructure.AICall.Sessions;
using SmartHopper.Infrastructure.AICall.Tools;
using SmartHopper.Infrastructure.Diagnostics;
using SmartHopper.Infrastructure.Streaming;
using Xunit;

namespace SmartHopper.Infrastructure.Tests.AICall.Sessions
{
    public class ConversationRecoveryTests
    {
        [Fact]
        public async Task AbandonedHistory_IsClosedBeforeNewUserMessage_WithoutExecutingMutation()
        {
            var call = Call("abandoned", "old-turn");
            var request = new Request
            {
                Body = AIBodyBuilder.Create().WithToolFilter("+gh_*").WithContextFilter("-*")
                    .Add(call).AddText(AIAgent.User, "Continue").Build(),
            };
            var executor = new Executor();
            request.OnExec = () =>
            {
                Assert.Equal(0, request.Body.PendingToolCallsCount());
                Assert.Same(call, request.Body.Interactions[0]);
                var recovered = Assert.IsType<AIInteractionToolResult>(request.Body.Interactions[1]);
                Assert.Equal(call.Id, recovered.Id);
                Assert.Equal(call.TurnId, recovered.TurnId);
                Assert.False(recovered.Result.Value<bool>("success"));
                Assert.Equal(AIAgent.User, request.Body.Interactions[2].Agent);
                return Result(Text());
            };
            var session = new ConversationSession(request, executor: executor);

            await session.RunToStableResult(new SessionOptions());

            Assert.Equal(0, executor.ToolExecutions);
            Assert.Equal("+gh_*", request.Body.ToolFilter);
            Assert.Single(request.Body.Interactions.OfType<AIInteractionToolResult>());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DisabledTools_HideAndRestoreFilters_AfterFailure(bool stream)
        {
            var request = new Request
            {
                Body = AIBodyBuilder.Create().WithToolFilter("+gh_get*").WithContextFilter("+selection")
                    .AddText(AIAgent.User, "test").Build(),
            };
            request.OnExec = () =>
            {
                Assert.Equal("-*", request.Body.ToolFilter);
                throw new InvalidOperationException("provider failed");
            };
            var session = new ConversationSession(request, executor: new Executor());
            if (stream)
            {
                await foreach (var result in session.Stream(new SessionOptions { ProcessTools = false }, new StreamingOptions()))
                {
                    Assert.Equal("+gh_get*", result.Body.ToolFilter);
                }
            }
            else
            {
                var result = await session.RunToStableResult(new SessionOptions { ProcessTools = false });
                Assert.Equal("+gh_get*", result.Body.ToolFilter);
            }

            Assert.Equal("+gh_get*", request.Body.ToolFilter);
            Assert.Equal("+selection", request.Body.ContextFilter);
            Assert.Equal("+gh_get*", session.GetHistoryReturn().Body.ToolFilter);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task InterruptedTool_ClosesEveryCall_AndNextRequestDoesNotReplay(bool cancelled)
        {
            var request = new Request { OnExec = () => Result(Call("one"), Call("two")) };
            var executor = new Executor
            {
                OnTool = tool => throw (cancelled ? new OperationCanceledException() : new InvalidOperationException("tool failed")),
            };
            var session = new ConversationSession(request, executor: executor);

            await session.RunToStableResult(new SessionOptions());

            Assert.Equal(0, request.Body.PendingToolCallsCount());
            Assert.Equal(2, request.Body.Interactions.OfType<AIInteractionToolResult>().Count());
            Assert.Equal(1, executor.ToolExecutions);
            request.OnExec = () => Result(Text());
            session.AddInteraction("Try another request");
            await session.RunToStableResult(new SessionOptions());
            Assert.Equal(1, executor.ToolExecutions);
        }

        [Fact]
        public async Task ToolPassLimit_ClosesUnexecutedCalls_AndRetainsOriginatingTurn()
        {
            var request = new Request { OnExec = () => Result(Call("budget")) };
            var executor = new Executor();
            var session = new ConversationSession(request, executor: executor);

            await session.RunToStableResult(new SessionOptions { MaxToolPasses = 0, MaxTurns = 1 });

            Assert.Equal(0, executor.ToolExecutions);
            Assert.Equal(0, request.Body.PendingToolCallsCount());
            var result = Assert.Single(request.Body.Interactions.OfType<AIInteractionToolResult>());
            var call = request.Body.Interactions.OfType<AIInteractionToolCall>().First(i => i.Agent == AIAgent.ToolCall);
            Assert.Equal(call.TurnId, result.TurnId);
            Assert.False(result.Metrics.HasProviderCallEvidence);
        }

        [Fact]
        public async Task StreamingFailure_ClosesAlreadyPersistedToolCalls()
        {
            var request = new Request();
            var executor = new Executor { Adapter = new InterruptedStream() };
            var session = new ConversationSession(request, executor: executor);

            await foreach (var _ in session.Stream(new SessionOptions(), new StreamingOptions())) { }

            Assert.Equal(0, request.Body.PendingToolCallsCount());
            Assert.Equal(0, executor.ToolExecutions);
            Assert.Single(request.Body.Interactions.OfType<AIInteractionToolResult>());
        }

        [Fact]
        public async Task BudgetExhaustion_ClosesCallsBeforeMutation_AndNextRunHasFreshBudget()
        {
            var call = Call("over-budget");
            call.Metrics = new SmartHopper.Infrastructure.AICall.Metrics.AIMetrics { InputTokensPrompt = 100, FinishReason = "tool_calls" };
            var request = new Request { OnExec = () => Result(call) };
            var executor = new Executor();
            var session = new ConversationSession(request, executor: executor);
            await session.RunToStableResult(new SessionOptions { MaxRunTokens = 100 });
            Assert.Equal(0, executor.ToolExecutions);
            Assert.Equal(0, request.Body.PendingToolCallsCount());
            request.OnExec = () => Result(Text());
            session.AddInteraction("Continue");
            var next = await session.RunToStableResult(new SessionOptions { MaxRunTokens = 100 });
            Assert.Contains(next.Body.Interactions.OfType<AIInteractionText>(), text => text.Content == "done");
        }

        [Fact]
        public async Task Cancel_OnlyCancelsActiveRun_AndNextRunCanExecute()
        {
            var request = new Request();
            var session = new ConversationSession(request, executor: new Executor());
            request.OnExec = () => { session.Cancel(); return Result(Call("cancelled-run")); };
            await session.RunToStableResult(new SessionOptions());
            Assert.Equal(0, request.Body.PendingToolCallsCount());
            var executed = false;
            request.OnExec = () => { executed = true; return Result(Text()); };
            session.AddInteraction("Continue after stop");
            await session.RunToStableResult(new SessionOptions());
            Assert.True(executed);
        }

        private static AIInteractionToolCall Call(string id, string turn = "provider-turn") => new ()
        {
            Id = id, Name = "gh_mutate", Arguments = new JObject(), TurnId = turn,
        };

        private static AIInteractionText Text() => new () { Agent = AIAgent.Assistant, Content = "done" };

        private static AIReturn Result(params IAIInteraction[] interactions)
        {
            var result = new AIReturn();
            result.SetBody(AIBodyBuilder.Create().AddRange(interactions).Build());
            return result;
        }

        private sealed class Request : AIRequestCall
        {
            public Func<AIReturn> OnExec { get; set; } = () => Result(Text());

            public override (bool IsValid, List<SHRuntimeMessage> Errors) IsValid() => (true, new ());

            public override Task<AIReturn> Exec(bool stream = false, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(this.OnExec());
            }
        }

        private sealed class Executor : IProviderExecutor
        {
            public int ToolExecutions { get; private set; }

            public Func<AIToolCall, AIReturn> OnTool { get; set; } = _ => Result();

            public IStreamingAdapter Adapter { get; set; }

            public Task<AIReturn?> ExecProviderAsync(AIRequestCall request, CancellationToken ct) => request.Exec(cancellationToken: ct)!;

            public Task<AIReturn?> ExecToolAsync(AIToolCall toolCall, CancellationToken ct)
            {
                this.ToolExecutions++;
                return Task.FromResult<AIReturn?>(this.OnTool(toolCall));
            }

            public IStreamingAdapter? TryGetStreamingAdapter(AIRequestCall request) => this.Adapter;
        }

        private sealed class InterruptedStream : IStreamingAdapter
        {
            public async IAsyncEnumerable<AIReturn> StreamAsync(AIRequestCall request, StreamingOptions options,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                yield return Result(Call("streamed"));
                await Task.Yield();
                throw new InvalidOperationException("stream interrupted");
            }
        }
    }
}
