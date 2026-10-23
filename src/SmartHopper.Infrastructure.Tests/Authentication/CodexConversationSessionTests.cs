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
using SmartHopper.Infrastructure.AICall.Core.Requests;
using SmartHopper.Infrastructure.AICall.Core.Returns;
using SmartHopper.Infrastructure.AICall.Sessions;
using SmartHopper.Infrastructure.Authentication.Codex;
using Xunit;

namespace SmartHopper.Infrastructure.Tests.Authentication
{
    public class CodexConversationSessionTests
    {
        [Fact]
        public async Task RunToStableResult_UsesThreadAndTurnAndStreamsAssistantText()
        {
            var transport = new FakeTransport();
            var observer = new FakeObserver();
            using var session = new CodexConversationSession(transport, "gpt-test", "system", observer);
            session.Configure("gpt-selected", "high");
            session.AddInteraction("Grasshopper 질문");

            var result = await session.RunToStableResult(new SessionOptions());

            Assert.Equal(new[] { "thread/start", "turn/start" }, transport.Methods);
            Assert.Contains(observer.Deltas, item => item.Content == "안녕하세요");
            Assert.Equal("안녕하세요", result.Body.Interactions.OfType<AIInteractionText>().Last().Content);
            Assert.Contains("Grasshopper 질문", transport.LastTurnInput);
            Assert.Equal("gpt-selected", transport.LastThreadModel);
            Assert.Equal("gpt-selected", transport.LastTurnModel);
            Assert.Equal("high", transport.LastTurnEffort);
            Assert.NotNull(observer.Final);
        }

        [Fact]
        public async Task GetModelsAsync_UsesVisibleCatalogAndReasoningLevels()
        {
            var transport = new FakeTransport();
            var observer = new FakeObserver();
            using var session = new CodexConversationSession(transport, string.Empty, "system", observer);

            var models = await session.GetModelsAsync();

            var model = Assert.Single(models);
            Assert.Equal("gpt-codex", model.Model);
            Assert.Equal("GPT Codex", model.DisplayName);
            Assert.True(model.IsDefault);
            Assert.Equal("medium", model.DefaultReasoningEffort);
            Assert.Equal(new[] { "low", "medium", "high" }, model.SupportedReasoningEfforts.Select(item => item.Value));
            Assert.Contains("model/list", transport.Methods);
        }

        [Fact]
        public async Task RunToStableResult_DynamicToolRequestExecutesAndTurnCompletes()
        {
            var transport = new FakeTransport { EmitDynamicToolCall = true };
            var observer = new FakeObserver();
            var bridge = new FakeDynamicToolBridge();
            using var session = new CodexConversationSession(
                transport,
                "gpt-test",
                "system",
                observer,
                dynamicToolBridge: bridge);
            session.AddInteraction("선택 객체를 확인해줘");

            var result = await session.RunToStableResult(new SessionOptions());

            Assert.Equal(1, bridge.ExecutionCount);
            Assert.Equal("gh_get_selected", bridge.LastCall?.Tool);
            Assert.Equal("server-request-1", transport.LastServerResponseId?.Value<string>());
            var response = Assert.IsType<JObject>(transport.LastServerResponseResult);
            Assert.True(response.Value<bool>("success"));
            var text = response["contentItems"]?[0]?.Value<string>("text");
            Assert.True(JObject.Parse(text ?? "{}").Value<bool>("observed"));
            Assert.Equal("smarthopper", transport.LastThreadStartParameters?["dynamicTools"]?[0]?.Value<string>("name"));
            Assert.Equal("안녕하세요", result.Body.Interactions.OfType<AIInteractionText>().Last().Content);
            Assert.Single(observer.ToolCalls);
            Assert.Single(observer.ToolResults);
        }

        [Fact]
        public async Task RunToStableResult_DisabledToolsRejectsCallsWithoutExecutingBridge()
        {
            var transport = new FakeTransport { EmitDynamicToolCall = true };
            var bridge = new FakeDynamicToolBridge();
            using var session = new CodexConversationSession(transport, "gpt-test", "system", new FakeObserver(), dynamicToolBridge: bridge);
            session.AddInteraction("Do not run tools");

            await session.RunToStableResult(new SessionOptions { ProcessTools = false });

            Assert.Equal(0, bridge.ExecutionCount);
            var response = Assert.IsType<JObject>(transport.LastServerResponseResult);
            Assert.False(response.Value<bool>("success"));
        }

        [Theory]
        [InlineData("other-thread", "turn-1", "smarthopper")]
        [InlineData("thread-1", "other-turn", "smarthopper")]
        [InlineData("thread-1", "turn-1", "unknown")]
        public async Task RunToStableResult_RejectsToolRequestOutsideActiveScope(
            string toolThreadId,
            string toolTurnId,
            string toolNamespace)
        {
            var transport = new FakeTransport
            {
                EmitDynamicToolCall = true,
                ToolThreadId = toolThreadId,
                ToolTurnId = toolTurnId,
                ToolNamespace = toolNamespace,
            };
            var bridge = new FakeDynamicToolBridge();
            using var session = new CodexConversationSession(
                transport,
                "gpt-test",
                "system",
                new FakeObserver(),
                dynamicToolBridge: bridge);
            session.AddInteraction("확인해줘");

            await session.RunToStableResult(new SessionOptions());

            Assert.Equal(0, bridge.ExecutionCount);
            var response = Assert.IsType<JObject>(transport.LastServerResponseResult);
            Assert.False(response.Value<bool>("success"));
        }

        [Fact]
        public async Task RunToStableResult_DuplicateCallIdExecutesOnlyOnce()
        {
            var transport = new FakeTransport { EmitDynamicToolCall = true, ToolRequestCount = 2 };
            var bridge = new FakeDynamicToolBridge();
            using var session = new CodexConversationSession(
                transport,
                "gpt-test",
                "system",
                new FakeObserver(),
                dynamicToolBridge: bridge);
            session.AddInteraction("확인해줘");

            await session.RunToStableResult(new SessionOptions());

            Assert.Equal(1, bridge.ExecutionCount);
            Assert.Equal(2, transport.ServerResponseCount);
            Assert.False(Assert.IsType<JObject>(transport.LastServerResponseResult).Value<bool>("success"));
        }

        [Fact]
        public async Task RunToStableResult_WithoutDynamicToolsSupportFallsBackToReadOnlyConversation()
        {
            var transport = new FakeTransport { SupportsDynamicTools = false };
            var observer = new FakeObserver();
            using var session = new CodexConversationSession(
                transport,
                "gpt-test",
                "system",
                observer,
                dynamicToolBridge: new FakeDynamicToolBridge());
            session.AddInteraction("질문");

            var result = await session.RunToStableResult(new SessionOptions());

            Assert.Null(transport.LastThreadStartParameters?["dynamicTools"]);
            Assert.Contains(
                result.Body.Interactions.OfType<AIInteractionText>(),
                item => item.Content.Contains("읽기 전용 대화 모드", StringComparison.Ordinal));
            Assert.Equal("안녕하세요", result.Body.Interactions.OfType<AIInteractionText>().Last().Content);
        }

        [Fact]
        public async Task TokenBudget_InterruptsBeforeDynamicTool_AndNextRunCanContinue()
        {
            var transport = new FakeTransport { EmitTokenUsage = true, EmitDynamicToolCall = true };
            var bridge = new FakeDynamicToolBridge();
            using var session = new CodexConversationSession(transport, "gpt-test", "system", new FakeObserver(), dynamicToolBridge: bridge);
            session.AddInteraction("Stop at the budget");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunToStableResult(new SessionOptions { MaxRunTokens = 100 }));
            Assert.Equal(0, bridge.ExecutionCount);
            await transport.InterruptSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("turn/interrupt", transport.Methods);
            transport.EmitTokenUsage = false;
            transport.EmitDynamicToolCall = false;
            session.AddInteraction("Continue with a fresh budget");
            var result = await session.RunToStableResult(new SessionOptions { MaxRunTokens = 100 });
            Assert.Contains(result.Body.Interactions.OfType<AIInteractionText>(), text => text.Content == "안녕하세요");
        }

        private sealed class FakeTransport : ICodexConversationTransport
        {
            public event EventHandler<CodexNotificationEventArgs>? NotificationReceived;

            public CodexServerRequestHandler? ServerRequestHandler { get; set; }

            public bool SupportsDynamicTools { get; set; } = true;

            public List<string> Methods { get; } = new ();

            public TaskCompletionSource<bool> InterruptSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public string LastTurnInput { get; private set; } = string.Empty;

            public string LastThreadModel { get; private set; } = string.Empty;

            public string LastTurnModel { get; private set; } = string.Empty;

            public string LastTurnEffort { get; private set; } = string.Empty;

            public bool EmitDynamicToolCall { get; set; }
            public bool EmitTokenUsage { get; set; }

            public string ToolThreadId { get; set; } = "thread-1";

            public string ToolTurnId { get; set; } = "turn-1";

            public string ToolNamespace { get; set; } = "smarthopper";

            public int ToolRequestCount { get; set; } = 1;

            public int ServerResponseCount { get; private set; }

            public JObject? LastThreadStartParameters { get; private set; }

            public JToken? LastServerResponseId { get; private set; }

            public JToken? LastServerResponseResult { get; private set; }

            public Task EnsureSignedInAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<JToken> SendAsync(
                string method,
                JObject? parameters = null,
                TimeSpan? timeout = null,
                CancellationToken cancellationToken = default)
            {
                this.Methods.Add(method);
                if (method == "turn/interrupt")
                {
                    this.InterruptSent.TrySetResult(true);
                    return Task.FromResult<JToken>(new JObject());
                }
                if (method == "model/list")
                {
                    return Task.FromResult<JToken>(new JObject
                    {
                        ["data"] = new JArray
                        {
                            new JObject
                            {
                                ["id"] = "gpt-codex-id",
                                ["model"] = "gpt-codex",
                                ["displayName"] = "GPT Codex",
                                ["description"] = "테스트 모델",
                                ["hidden"] = false,
                                ["isDefault"] = true,
                                ["defaultReasoningEffort"] = "medium",
                                ["supportedReasoningEfforts"] = new JArray
                                {
                                    new JObject { ["reasoningEffort"] = "low", ["description"] = "낮음" },
                                    new JObject { ["reasoningEffort"] = "medium", ["description"] = "보통" },
                                    new JObject { ["reasoningEffort"] = "high", ["description"] = "높음" },
                                },
                            },
                            new JObject
                            {
                                ["id"] = "hidden",
                                ["model"] = "hidden",
                                ["displayName"] = "Hidden",
                                ["hidden"] = true,
                            },
                        },
                    });
                }

                if (method == "thread/start")
                {
                    this.LastThreadModel = parameters?.Value<string>("model") ?? string.Empty;
                    this.LastThreadStartParameters = parameters != null ? (JObject)parameters.DeepClone() : null;
                    return Task.FromResult<JToken>(new JObject
                    {
                        ["thread"] = new JObject { ["id"] = "thread-1" },
                    });
                }

                if (method == "turn/start")
                {
                    this.LastTurnInput = parameters?["input"]?[0]?.Value<string>("text") ?? string.Empty;
                    this.LastTurnModel = parameters?.Value<string>("model") ?? string.Empty;
                    this.LastTurnEffort = parameters?.Value<string>("effort") ?? string.Empty;
                    _ = Task.Run(async () =>
                    {
                        await Task.Yield();
                        if (this.EmitTokenUsage)
                        {
                            this.Raise("thread/tokenUsage/updated", new JObject
                            {
                                ["threadId"] = "thread-1", ["turnId"] = "turn-1",
                                ["tokenUsage"] = new JObject { ["total"] = new JObject { ["totalTokens"] = 100 }, ["last"] = new JObject { ["totalTokens"] = 100 } },
                            });
                            return;
                        }
                        if (this.EmitDynamicToolCall && this.ServerRequestHandler != null)
                        {
                            for (var index = 0; index < this.ToolRequestCount; index++)
                            {
                                await this.ServerRequestHandler(
                                    new CodexServerRequest(
                                        new JValue($"server-request-{index + 1}"),
                                        "item/tool/call",
                                        new JObject
                                        {
                                            ["threadId"] = this.ToolThreadId,
                                            ["turnId"] = this.ToolTurnId,
                                            ["callId"] = "call-1",
                                            ["namespace"] = this.ToolNamespace,
                                            ["tool"] = "gh_get_selected",
                                            ["arguments"] = new JObject(),
                                        }),
                                    CancellationToken.None);
                            }
                        }
                        this.Raise("item/agentMessage/delta", new JObject
                        {
                            ["threadId"] = "thread-1",
                            ["turnId"] = "turn-1",
                            ["itemId"] = "item-1",
                            ["delta"] = "안녕하세요",
                        });
                        this.Raise("turn/completed", new JObject
                        {
                            ["threadId"] = "thread-1",
                            ["turn"] = new JObject
                            {
                                ["id"] = "turn-1",
                                ["status"] = "completed",
                                ["items"] = new JArray
                                {
                                    new JObject
                                    {
                                        ["id"] = "item-1",
                                        ["type"] = "agentMessage",
                                        ["text"] = "안녕하세요",
                                    },
                                },
                            },
                        });
                    });
                    return Task.FromResult<JToken>(new JObject
                    {
                        ["turn"] = new JObject { ["id"] = "turn-1", ["status"] = "inProgress", ["items"] = new JArray() },
                    });
                }

                throw new InvalidOperationException(method);
            }

            public Task SendResponseAsync(JToken requestId, JToken? result, CancellationToken cancellationToken = default)
            {
                this.LastServerResponseId = requestId.DeepClone();
                this.LastServerResponseResult = result?.DeepClone();
                this.ServerResponseCount++;
                return Task.CompletedTask;
            }

            public Task SendErrorAsync(JToken requestId, int code, string message, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            private void Raise(string method, JObject parameters)
            {
                this.NotificationReceived?.Invoke(this, new CodexNotificationEventArgs(method, parameters));
            }
        }

        private sealed class FakeDynamicToolBridge : ICodexDynamicToolBridge
        {
            public int ExecutionCount { get; private set; }

            public CodexDynamicToolCall? LastCall { get; private set; }

            public JArray BuildDynamicTools()
            {
                return new JArray
                {
                    new JObject
                    {
                        ["type"] = "namespace",
                        ["name"] = "smarthopper",
                        ["description"] = "테스트 Canvas 도구",
                        ["tools"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "function",
                                ["name"] = "gh_get_selected",
                                ["description"] = "선택 객체 조회",
                                ["inputSchema"] = new JObject { ["type"] = "object" },
                            },
                        },
                    },
                };
            }

            public Task<CodexDynamicToolResult> ExecuteAsync(CodexDynamicToolCall call, CancellationToken cancellationToken)
            {
                this.ExecutionCount++;
                this.LastCall = call;
                return Task.FromResult(CodexDynamicToolResult.Ok(new JObject { ["observed"] = true }));
            }

            public void CancelPending()
            {
            }

            public void Dispose()
            {
            }
        }

        private sealed class FakeObserver : IConversationObserver
        {
            public List<AIInteractionText> Deltas { get; } = new ();

            public AIReturn? Final { get; private set; }

            public List<AIInteractionToolCall> ToolCalls { get; } = new ();

            public List<AIInteractionToolResult> ToolResults { get; } = new ();

            public void OnStart(AIRequestCall request)
            {
            }

            public void OnDelta(IAIInteraction interaction)
            {
                if (interaction is AIInteractionText text)
                {
                    this.Deltas.Add(text);
                }
            }

            public void OnInteractionCompleted(IAIInteraction interaction)
            {
            }

            public void OnToolCall(AIInteractionToolCall toolCall)
            {
                this.ToolCalls.Add(toolCall);
            }

            public void OnToolResult(AIInteractionToolResult toolResult)
            {
                this.ToolResults.Add(toolResult);
            }

            public void OnFinal(AIReturn finalResult)
            {
                this.Final = finalResult;
            }

            public void OnError(Exception error)
            {
            }
        }
    }
}
