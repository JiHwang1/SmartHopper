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
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.Core.Returns;
using SmartHopper.Infrastructure.AICall.Tools;
using SmartHopper.Infrastructure.AITools;
using SmartHopper.Infrastructure.AITools.External;
using SmartHopper.Infrastructure.Authentication.Codex;
using Xunit;

namespace SmartHopper.Infrastructure.Tests.AITools
{
    public class AIToolExternalAdapterTests
    {
        [Fact]
        public void Catalog_AllowListDoesNotImplicitlyEnableMutation()
        {
            var tools = BuildCatalog(("gh_put", true, true, "{\"type\":\"object\"}"));
            var catalog = new AIToolExternalCatalog(() => tools);
            var options = new AIToolExternalCatalogOptions
            {
                EnabledTools = new[] { "gh_put" },
                ExposeMutatingTools = false,
            };

            Assert.False(catalog.IsExposed("gh_put", options));
        }

        [Fact]
        public void Catalog_AppliesEnabledAllowListMutationAndSchemaRules()
        {
            var tools = BuildCatalog(
                ("gh_get", false, true, "{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\"}}}"),
                ("gh_put", true, true, "invalid-schema"),
                ("disabled", false, false, "{\"type\":\"object\"}"));
            var catalog = new AIToolExternalCatalog(() => tools);
            var options = new AIToolExternalCatalogOptions
            {
                EnabledTools = new[] { "gh_get", "gh_put", "disabled" },
                ExposeMutatingTools = true,
            };

            var descriptors = catalog.BuildDescriptors(options);

            Assert.Equal(new[] { "gh_get", "gh_put" }, descriptors.Select(tool => tool.Name));
            Assert.Equal("string", (string?)descriptors[0].InputSchema["properties"]?["q"]?["type"]);
            Assert.Equal("object", (string?)descriptors[1].InputSchema["type"]);
        }

        [Fact]
        public void Catalog_DestructiveFilterOverridesAllowList()
        {
            var destructive = new AITool(
                name: "gh_remove",
                description: "삭제 테스트",
                category: "Test",
                parametersSchema: "{\"type\":\"object\"}",
                execute: _ => Task.FromResult(new AIReturn()),
                mutatesCanvas: true,
                annotations: new AIToolAnnotations(destructiveHint: true));
            var catalog = new AIToolExternalCatalog(() => new Dictionary<string, AITool>
            {
                [destructive.Name] = destructive,
            });

            var exposed = catalog.IsExposed("gh_remove", new AIToolExternalCatalogOptions
            {
                EnabledTools = new[] { "gh_remove" },
                ExposeMutatingTools = true,
                ExcludeDestructiveTools = true,
            });

            Assert.False(exposed);
        }

        [Fact]
        public async Task Executor_PreservesCallIdentityAndMapsSuccess()
        {
            var tools = BuildCatalog(("gh_get", false, true, "{\"type\":\"object\"}"));
            var catalog = new AIToolExternalCatalog(() => tools);
            var executor = new AIToolExternalExecutor(catalog, call =>
            {
                var interaction = call.GetToolCall();
                var result = new AIReturn
                {
                    Request = call,
                    SkipRequestValidation = true,
                    SkipMetricsValidation = true,
                };
                result.SetBody(AIBody.Empty.WithAppended(new AIInteractionToolResult
                {
                    Id = interaction.Id,
                    Name = interaction.Name,
                    TurnId = interaction.TurnId,
                    Result = new JObject
                    {
                        ["callId"] = interaction.Id,
                        ["turnId"] = interaction.TurnId,
                    },
                }));
                return Task.FromResult(result);
            });

            var result = await executor.ExecuteAsync(
                "gh_get",
                new JObject(),
                new AIToolExternalCatalogOptions(),
                callId: "call-1",
                turnId: "turn-1");

            Assert.False(result.IsError);
            Assert.Equal("call-1", (string?)result.Payload["callId"]);
            Assert.Equal("turn-1", (string?)result.Payload["turnId"]);
        }

        [Fact]
        public async Task Executor_SanitizesThrownException()
        {
            var tools = BuildCatalog(("gh_get", false, true, "{\"type\":\"object\"}"));
            var catalog = new AIToolExternalCatalog(() => tools);
            var executor = new AIToolExternalExecutor(
                catalog,
                _ => throw new InvalidOperationException("oauth-token-sensitive-value"));

            var result = await executor.ExecuteAsync(
                "gh_get",
                new JObject(),
                new AIToolExternalCatalogOptions());

            Assert.True(result.IsError);
            Assert.DoesNotContain("oauth-token-sensitive-value", result.ErrorMessage!);
            Assert.Contains("failed to execute", result.ErrorMessage!);
        }

        [Fact]
        public async Task CodexBridge_SerializesConcurrentCalls()
        {
            var tools = BuildCatalog(("gh_get", false, true, "{\"type\":\"object\"}"));
            var catalog = new AIToolExternalCatalog(() => tools);
            var active = 0;
            var maximum = 0;
            var executor = new AIToolExternalExecutor(catalog, async call =>
            {
                var current = Interlocked.Increment(ref active);
                maximum = Math.Max(maximum, current);
                await Task.Delay(20, call.CancellationToken);
                Interlocked.Decrement(ref active);
                return CreateSuccessfulToolReturn(call);
            });
            using var bridge = new CodexDynamicToolBridge(
                catalog,
                executor,
                new AIToolExternalCatalogOptions { EnabledTools = new[] { "gh_get" } });

            await Task.WhenAll(
                bridge.ExecuteAsync(CreateCodexCall("call-1"), CancellationToken.None),
                bridge.ExecuteAsync(CreateCodexCall("call-2"), CancellationToken.None));

            Assert.Equal(1, maximum);
        }

        [Fact]
        public async Task CodexBridge_CancelPendingCancelsExecutionAndReturnsLimitedError()
        {
            var tools = BuildCatalog(("gh_get", false, true, "{\"type\":\"object\"}"));
            var catalog = new AIToolExternalCatalog(() => tools);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var executor = new AIToolExternalExecutor(catalog, async call =>
            {
                started.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, call.CancellationToken);
                return CreateSuccessfulToolReturn(call);
            });
            using var bridge = new CodexDynamicToolBridge(
                catalog,
                executor,
                new AIToolExternalCatalogOptions { EnabledTools = new[] { "gh_get" } });

            var execution = bridge.ExecuteAsync(CreateCodexCall("call-1"), CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            bridge.CancelPending();
            var result = await execution;

            Assert.False(result.Success);
            Assert.DoesNotContain("TaskCanceledException", result.ErrorMessage ?? string.Empty);
        }

        [Fact]
        public async Task CodexBridge_DoesNotExposeExecutorErrorDetail()
        {
            var tools = BuildCatalog(("gh_get", false, true, "{\"type\":\"object\"}"));
            var catalog = new AIToolExternalCatalog(() => tools);
            var executor = new AIToolExternalExecutor(
                catalog,
                _ => throw new InvalidOperationException("credential-sensitive-detail"));
            using var bridge = new CodexDynamicToolBridge(
                catalog,
                executor,
                new AIToolExternalCatalogOptions { EnabledTools = new[] { "gh_get" } });

            var result = await bridge.ExecuteAsync(CreateCodexCall("call-1"), CancellationToken.None);

            Assert.False(result.Success);
            Assert.DoesNotContain("credential-sensitive-detail", result.ErrorMessage ?? string.Empty);
        }

        private static CodexDynamicToolCall CreateCodexCall(string callId)
        {
            return new CodexDynamicToolCall
            {
                ThreadId = "thread-1",
                TurnId = "turn-1",
                CallId = callId,
                Namespace = "smarthopper",
                Tool = "gh_get",
                Arguments = new JObject(),
            };
        }

        private static AIReturn CreateSuccessfulToolReturn(AIToolCall call)
        {
            var interaction = call.GetToolCall();
            var result = new AIReturn { Request = call, SkipRequestValidation = true, SkipMetricsValidation = true };
            result.SetBody(AIBody.Empty.WithAppended(new AIInteractionToolResult
            {
                Id = interaction.Id,
                Name = interaction.Name,
                TurnId = interaction.TurnId,
                Result = new JObject { ["ok"] = true },
            }));
            return result;
        }

        private static IReadOnlyDictionary<string, AITool> BuildCatalog(
            params (string name, bool mutatesCanvas, bool enabled, string schema)[] entries)
        {
            return entries.ToDictionary(
                entry => entry.name,
                entry => new AITool(
                    name: entry.name,
                    description: $"Test tool {entry.name}",
                    category: "Test",
                    parametersSchema: entry.schema,
                    execute: _ => Task.FromResult(new AIReturn()),
                    mutatesCanvas: entry.mutatesCanvas,
                    enabled: entry.enabled));
        }
    }
}
