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

namespace SmartHopper.Infrastructure.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Newtonsoft.Json.Linq;
    using SmartHopper.Infrastructure.AICall.Core.Base;
    using SmartHopper.Infrastructure.AICall.Core.Interactions;
    using SmartHopper.Infrastructure.AICall.Core.Returns;
    using SmartHopper.Infrastructure.AICall.Tools;
    using SmartHopper.Infrastructure.AITools;
    using SmartHopper.Infrastructure.Diagnostics;
    using Xunit;

    [Collection("AIToolManager")]
    public class AIToolCallExecutionTests
    {
        [Fact]
        public async Task Exec_TimeoutCancelsToolTokenAndReturnsError()
        {
            AIToolManager.ResetTools();
            var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            AIToolManager.RegisterTool(new AITool("timeout_tool", "Timeout tool", "test", "{}", async request =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, request.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult(true);
                    throw;
                }

                return CreateResult();
            }));

            var call = CreateCall("timeout_tool");
            call.TimeoutSeconds = 1;

            var result = await call.Exec().ConfigureAwait(false);

            Assert.True(result.Messages.Exists(message => message.Severity == SHRuntimeMessageSeverity.Error));
            Assert.Contains(result.Messages, message => message.Message.Contains("exceeded", StringComparison.OrdinalIgnoreCase));
            Assert.True(await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false));
        }

        [Fact]
        public async Task Exec_CallerCancellationCancelsToolTokenAndReturnsError()
        {
            AIToolManager.ResetTools();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            AIToolManager.RegisterTool(new AITool("cancel_tool", "Cancel tool", "test", "{}", async request =>
            {
                started.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, request.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult(true);
                    throw;
                }

                return CreateResult();
            }));

            using (var callerCancellation = new CancellationTokenSource())
            {
                var call = CreateCall("cancel_tool");
                var execution = call.Exec(callerCancellation.Token);
                Assert.True(await started.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false));
                callerCancellation.Cancel();

                var result = await execution.ConfigureAwait(false);

                Assert.True(result.Messages.Exists(message => message.Severity == SHRuntimeMessageSeverity.Error));
                Assert.Contains(result.Messages, message => message.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
                Assert.True(await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false));
            }
        }

        [Fact]
        public async Task Exec_ValidatesOnce_WhilePublicManagerStillValidates()
        {
            AIToolManager.ResetTools();
            AIToolManager.RegisterTool(new AITool("count_validation", "Validation tool", "test", "{}", _ => Task.FromResult(CreateResult())));

            var execCall = new CountingToolCall("count_validation");
            var execResult = await execCall.Exec().ConfigureAwait(false);

            Assert.Equal(1, execCall.ValidationCount);
            Assert.NotNull(execResult.Body);

            var managerCall = new CountingToolCall("count_validation");
            var managerResult = await AIToolManager.ExecuteTool(managerCall).ConfigureAwait(false);

            Assert.Equal(1, managerCall.ValidationCount);
            Assert.NotNull(managerResult.Body);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Exec_CancelledMutationRetainsExecutionUntilActualCompletion(bool timeout)
        {
            AIToolManager.ResetTools();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            AIToolManager.RegisterTool(new AITool("pending_mutation", "Pending mutation", "test", "{}", async request =>
            {
                using var registration = request.CancellationToken.Register(() => cancelled.TrySetResult(true));
                started.TrySetResult(true);
                // Simulate a native operation that cannot be interrupted after it starts.
                await finish.Task.ConfigureAwait(false);
                return CreateResult();
            }, mutatesCanvas: true));

            using var caller = new CancellationTokenSource();
            var call = CreateCall("pending_mutation");
            call.TimeoutSeconds = timeout ? 1 : 30;
            var execution = call.Exec(caller.Token);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                if (!timeout) caller.Cancel();
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                Assert.False(execution.IsCompleted);
            }
            finally
            {
                finish.TrySetResult(true);
            }

            var result = await execution.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            Assert.Contains(result.Messages, message => message.Severity == SHRuntimeMessageSeverity.Error);
        }

        private static CountingToolCall CreateCall(string name)
        {
            return new CountingToolCall(name);
        }

        private static AIReturn CreateResult()
        {
            var result = new AIReturn();
            result.SetBody(AIBodyBuilder.Create()
                .WithTurnId(Guid.NewGuid().ToString("N"))
                .AddText(AIAgent.ToolResult, "done")
                .Build());
            return result;
        }

        private sealed class CountingToolCall : AIToolCall
        {
            public CountingToolCall(string name)
            {
                this.Provider = "test";
                this.Model = "test-model";
                this.Body = AIBodyBuilder.Create()
                    .WithTurnId(Guid.NewGuid().ToString("N"))
                    .Add(new AIInteractionToolCall
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Name = name,
                        Arguments = new JObject(),
                    })
                    .Build();
            }

            public int ValidationCount { get; private set; }

            public override (bool IsValid, List<SHRuntimeMessage> Errors) IsValid()
            {
                this.ValidationCount++;
                return base.IsValid();
            }
        }
    }
}
