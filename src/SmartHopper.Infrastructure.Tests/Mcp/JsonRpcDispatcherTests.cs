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

namespace SmartHopper.Infrastructure.Tests.Mcp
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Newtonsoft.Json.Linq;
    using SmartHopper.Infrastructure.AICall.Core.Interactions;
    using SmartHopper.Infrastructure.AICall.Core.Returns;
    using SmartHopper.Infrastructure.AICall.Tools;
    using SmartHopper.Infrastructure.AITools;
    using SmartHopper.Infrastructure.Mcp;
    using Xunit;

    /// <summary>
    /// Unit tests for <see cref="JsonRpcDispatcher"/>. No Rhino/Grasshopper dependencies.
    /// </summary>
    public class JsonRpcDispatcherTests
    {
        private const string ReadOnlySchema = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}";

        [Fact]
        public async Task Dispatch_Initialize_ReturnsServerInfoAndProtocolVersion()
        {
            var dispatcher = BuildDispatcher(new McpServerOptions
            {
                ServerName = "smarthopper",
                ServerVersion = "9.9.9",
            });

            var raw = await dispatcher.DispatchAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}");

            Assert.NotNull(raw);
            var obj = JObject.Parse(raw!);
            Assert.Equal("2.0", (string?)obj["jsonrpc"]);
            Assert.Equal(1, (int?)obj["id"]);
            Assert.NotNull(obj["result"]);
            Assert.Equal("smarthopper", (string?)obj["result"]?["serverInfo"]?["name"]);
            Assert.Equal("9.9.9", (string?)obj["result"]?["serverInfo"]?["version"]);
            Assert.NotNull((string?)obj["result"]?["protocolVersion"]);
            Assert.NotNull(obj["result"]?["capabilities"]?["tools"]);
            Assert.NotNull(obj["result"]?["capabilities"]?["resources"]);
            Assert.NotNull(obj["result"]?["capabilities"]?["prompts"]);
        }

        [Fact]
        public async Task Dispatch_ToolsList_ReturnsExposedToolsOnly()
        {
            var dispatcher = BuildDispatcher(new McpServerOptions(),
                ("gh_get", ReadOnlySchema, false),
                ("gh_put", ReadOnlySchema, true));

            var raw = await dispatcher.DispatchAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");

            var obj = JObject.Parse(raw!);
            var tools = (JArray?)obj["result"]?["tools"];
            Assert.NotNull(tools);
            Assert.Single(tools!);
            Assert.Equal("gh_get", (string?)tools![0]["name"]);
            Assert.Equal("object", (string?)tools[0]["inputSchema"]?["type"]);
        }

        [Fact]
        public async Task Dispatch_ToolsCall_ReturnsTextContentWithToolResult()
        {
            var dispatcher = BuildDispatcher(new McpServerOptions(),
                executor: call =>
                {
                    var ret = new AIReturn
                    {
                        Request = call,
                        SkipRequestValidation = true,
                        SkipMetricsValidation = true,
                    };
                    ret.SetBody(AIBody.Empty.WithAppended(new AIInteractionToolResult
                    {
                        Name = call.GetToolCall().Name,
                        Result = new JObject { ["ok"] = true, ["echoed"] = call.GetToolCall().Arguments },
                    }));
                    return Task.FromResult(ret);
                },
                tools: ("gh_get", ReadOnlySchema, false));

            var raw = await dispatcher.DispatchAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"gh_get\",\"arguments\":{\"path\":\"/foo\"}}}");

            var obj = JObject.Parse(raw!);
            Assert.Equal(false, (bool?)obj["result"]?["isError"]);
            var text = (string?)obj["result"]?["content"]?[0]?["text"];
            Assert.NotNull(text);
            var payload = JObject.Parse(text!);
            Assert.Equal(true, (bool?)payload["ok"]);
            Assert.Equal("/foo", (string?)payload["echoed"]?["path"]);
        }

        [Fact]
        public async Task Dispatch_UnknownMethod_ReturnsMethodNotFoundError()
        {
            var dispatcher = BuildDispatcher(new McpServerOptions());

            var raw = await dispatcher.DispatchAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"unknown/method\"}");

            var obj = JObject.Parse(raw!);
            Assert.Equal(-32601, (int?)obj["error"]?["code"]);
        }

        [Fact]
        public async Task DefaultServerDispatcher_RegistersEmbeddedResourcesAndPrompts()
        {
            using var dispatcher = McpServer.BuildDefaultDispatcher(new McpServerOptions());

            var resources = JObject.Parse((await dispatcher.DispatchAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"resources/list\"}"))!);
            var resourceList = (JArray)resources["result"]!["resources"]!;
            Assert.NotEmpty(resourceList);
            Assert.Contains(resourceList, resource => (string?)resource["uri"] == "docs:///grasshopper-foundations");
            Assert.DoesNotContain(resourceList, resource => (string?)resource["uri"] == "docs:///assistant-core");

            var resourceRead = JObject.Parse((await dispatcher.DispatchAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"resources/read\",\"params\":{\"uri\":\"docs:///grasshopper-foundations\"}}"))!);
            Assert.Equal("2.0", (string?)resourceRead["jsonrpc"]);
            Assert.Equal("text/markdown", (string?)resourceRead["result"]?["contents"]?[0]?["mimeType"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)resourceRead["result"]?["contents"]?[0]?["text"]));

            var prompts = JObject.Parse((await dispatcher.DispatchAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"prompts/list\"}"))!);
            Assert.Contains((JArray)prompts["result"]!["prompts"]!, prompt => (string?)prompt["name"] == "grasshopper-expert");

            var prompt = JObject.Parse((await dispatcher.DispatchAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":13,\"method\":\"prompts/get\",\"params\":{\"name\":\"grasshopper-expert\"}}"))!);
            var messages = (JArray)prompt["result"]!["messages"]!;
            Assert.Single(messages);
            Assert.Equal("user", (string?)messages[0]?["role"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)messages[0]?["content"]?["text"]));
        }

        [Fact]
        public async Task Dispatch_ResourcesRead_MissingUri_ReturnsInvalidParams()
        {
            using var dispatcher = McpServer.BuildDefaultDispatcher(new McpServerOptions());

            var response = JObject.Parse((await dispatcher.DispatchAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":\"missing-uri\",\"method\":\"resources/read\",\"params\":{}}"))!);

            Assert.Equal("2.0", (string?)response["jsonrpc"]);
            Assert.Equal("missing-uri", (string?)response["id"]);
            Assert.Equal(-32602, (int?)response["error"]?["code"]);
            Assert.Null(response["result"]);
        }

        [Theory]
        [InlineData("docs:///does-not-exist")]
        [InlineData("docs:///assistant-core")]
        [InlineData("file:///C:/Windows/win.ini")]
        [InlineData("docs:///../../secret.md")]
        [InlineData("https://example.com/secret.md")]
        [InlineData("docs:///grasshopper-foundations/extra")]
        public async Task Dispatch_ResourcesRead_UnknownInternalAndArbitraryUris_ReturnsResourceNotFound(string resourceUri)
        {
            using var dispatcher = McpServer.BuildDefaultDispatcher(new McpServerOptions());

            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 20,
                ["method"] = "resources/read",
                ["params"] = new JObject { ["uri"] = resourceUri },
            };
            var response = JObject.Parse((await dispatcher.DispatchAsync(request.ToString()))!);

            Assert.Equal("2.0", (string?)response["jsonrpc"]);
            Assert.Equal(20, (int?)response["id"]);
            Assert.Equal(-32002, (int?)response["error"]?["code"]);
            Assert.Null(response["result"]);
        }

        [Theory]
        [InlineData("{}", -32602)]
        [InlineData("{\"name\":\"does-not-exist\"}", -32602)]
        public async Task Dispatch_PromptsGet_InvalidOrUnknownRequest_ReturnsInvalidParams(
            string parameters,
            int expectedCode)
        {
            using var dispatcher = McpServer.BuildDefaultDispatcher(new McpServerOptions());
            var request = $"{{\"jsonrpc\":\"2.0\",\"id\":\"prompt-id\",\"method\":\"prompts/get\",\"params\":{parameters}}}";

            var response = JObject.Parse((await dispatcher.DispatchAsync(request))!);

            Assert.Equal("2.0", (string?)response["jsonrpc"]);
            Assert.Equal("prompt-id", (string?)response["id"]);
            Assert.Equal(expectedCode, (int?)response["error"]?["code"]);
            Assert.Null(response["result"]);
        }

        [Theory]
        [InlineData("resources/read", "{\"uri\":\"docs:///assistant-core\"}")]
        [InlineData("prompts/get", "{\"name\":\"does-not-exist\"}")]
        public async Task Dispatch_ResourceAndPromptFailuresForNotifications_DoNotReply(
            string method,
            string parameters)
        {
            using var dispatcher = McpServer.BuildDefaultDispatcher(new McpServerOptions());
            var request = $"{{\"jsonrpc\":\"2.0\",\"method\":\"{method}\",\"params\":{parameters}}}";

            Assert.Null(await dispatcher.DispatchAsync(request));
        }

        [Fact]
        public async Task Dispatch_ToolsCall_EmitsOnlySafeImageBlocksWithoutBase64Text()
        {
            const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
            using var dispatcher = BuildDispatcher(
                new McpServerOptions(),
                executor: call =>
                {
                    var toolResult = new AIInteractionToolResult
                    {
                        Name = call.GetToolCall().Name,
                        Result = new JObject { ["ok"] = true },
                    };
                    toolResult.Images.Add(new ToolResultImage { ImageData = png, MimeType = "image/png" });
                    toolResult.Images.Add(new ToolResultImage { ImageData = "not-base64", MimeType = "image/png" });
                    toolResult.Images.Add(new ToolResultImage { ImageData = png, MimeType = "text/html" });

                    var response = new AIReturn
                    {
                        Request = call,
                        SkipRequestValidation = true,
                        SkipMetricsValidation = true,
                    };
                    response.SetBody(AIBody.Empty.WithAppended(toolResult));
                    return Task.FromResult(response);
                },
                tools: ("gh_get", ReadOnlySchema, false));

            var responseObject = JObject.Parse((await dispatcher.DispatchAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":21,\"method\":\"tools/call\",\"params\":{\"name\":\"gh_get\",\"arguments\":{}}}"))!);
            var content = (JArray)responseObject["result"]!["content"]!;

            Assert.Equal(2, content.Count);
            Assert.Equal("text", (string?)content[0]?["type"]);
            Assert.DoesNotContain(png, (string?)content[0]?["text"]);
            Assert.DoesNotContain("imageBase64", (string?)content[0]?["text"]);
            Assert.Equal("image", (string?)content[1]?["type"]);
            Assert.Equal(png, (string?)content[1]?["data"]);
            Assert.Equal("image/png", (string?)content[1]?["mimeType"]);
        }

        [Fact]
        public void EmbeddedMcpResourceLoader_ReturnsCachedText()
        {
            var first = EmbeddedMcpResourceLoader.ReadMarkdown("grasshopper-foundations.md");
            var second = EmbeddedMcpResourceLoader.ReadMarkdown("grasshopper-foundations.md");

            Assert.False(string.IsNullOrWhiteSpace(first));
            Assert.Same(first, second);
        }

        [Fact]
        public async Task Dispatch_InvalidJson_ReturnsParseError()
        {
            var dispatcher = BuildDispatcher(new McpServerOptions());

            var raw = await dispatcher.DispatchAsync("not-json");

            var obj = JObject.Parse(raw!);
            Assert.Equal(-32700, (int?)obj["error"]?["code"]);
        }

        [Fact]
        public async Task Dispatch_Notification_ReturnsNoResponse()
        {
            var dispatcher = BuildDispatcher(new McpServerOptions());

            var raw = await dispatcher.DispatchAsync(
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");

            Assert.Null(raw);
        }

        [Theory]
        [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"unknown\"}")]
        [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"params\":1}")]
        [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"tools/call\",\"params\":[]}")]
        public async Task Dispatch_NotificationErrors_NeverProduceReplies(string request)
        {
            using var dispatcher = BuildDispatcher(new McpServerOptions());
            Assert.Null(await dispatcher.DispatchAsync(request));
        }

        [Fact]
        public async Task Dispatch_NotificationHandlerThrows_DoesNotReply()
        {
            var options = new McpServerOptions();
            using var dispatcher = new JsonRpcDispatcher(options, new AIToolMcpAdapter(
                options,
                () => throw new System.InvalidOperationException("test failure"),
                _ => Task.FromResult(new AIReturn())));
            Assert.Null(await dispatcher.DispatchAsync("{\"jsonrpc\":\"2.0\",\"method\":\"tools/list\"}"));
        }

        [Theory]
        [InlineData("null")]
        [InlineData("1")]
        [InlineData("[]")]
        [InlineData("{\"method\":\"ping\",\"id\":1}")]
        [InlineData("{\"jsonrpc\":[],\"method\":\"ping\",\"id\":1}")]
        [InlineData("{\"jsonrpc\":\"1.0\",\"method\":\"ping\",\"id\":1}")]
        [InlineData("{\"jsonrpc\":\"2.0\",\"method\":1,\"id\":1}")]
        [InlineData("{\"jsonrpc\":\"2.0\",\"method\":{},\"id\":1}")]
        [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"id\":true}")]
        [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"id\":[]}")]
        [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"id\":1,\"result\":{}}")]
        public async Task Dispatch_InvalidEnvelope_ReturnsInvalidRequestWithNullId(string request)
        {
            using var dispatcher = BuildDispatcher(new McpServerOptions());
            var response = JObject.Parse((await dispatcher.DispatchAsync(request))!);
            Assert.Equal(-32600, response["error"]!.Value<int>("code"));
            Assert.Equal(JTokenType.Null, response["id"]!.Type);
            Assert.Null(response["result"]);
        }

        [Theory]
        [InlineData("1")]
        [InlineData("[]")]
        [InlineData("null")]
        public async Task Dispatch_UnsupportedParameters_ReturnsInvalidParams(string parameters)
        {
            using var dispatcher = BuildDispatcher(new McpServerOptions());
            var response = JObject.Parse((await dispatcher.DispatchAsync(
                $"{{\"jsonrpc\":\"2.0\",\"id\":\"typed-id\",\"method\":\"ping\",\"params\":{parameters}}}"))!);
            Assert.Equal("typed-id", response.Value<string>("id"));
            Assert.Equal(-32602, response["error"]!.Value<int>("code"));
        }

        [Theory]
        [InlineData("null", "ping")]
        [InlineData("\"request-id\"", "initialized")]
        public async Task Dispatch_ExplicitId_IsARequestAndGetsAResponse(string id, string method)
        {
            using var dispatcher = BuildDispatcher(new McpServerOptions());
            var response = JObject.Parse((await dispatcher.DispatchAsync(
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\"}}"))!);
            Assert.True(JToken.DeepEquals(JToken.Parse(id), response["id"]));
            Assert.NotNull(response["result"]);
            Assert.Null(response["error"]);
        }

        [Fact]
        public async Task Dispatch_ToolsCall_MissingName_ReturnsToolError()
        {
            var dispatcher = BuildDispatcher(new McpServerOptions());

            var raw = await dispatcher.DispatchAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{}}");

            var obj = JObject.Parse(raw!);
            Assert.Equal(true, (bool?)obj["result"]?["isError"]);
            Assert.Contains("name", (string?)obj["result"]?["content"]?[0]?["text"]);
        }

        [Fact]
        public async Task Dispatch_ToolsCall_PreservesToolErrorAndForwardsCancellationToken()
        {
            using var cancellation = new CancellationTokenSource();
            CancellationToken observed = default;
            using var dispatcher = BuildDispatcher(
                new McpServerOptions(),
                executor: call =>
                {
                    observed = call.CancellationToken;
                    var response = new AIReturn
                    {
                        Request = call,
                        SkipRequestValidation = true,
                        SkipMetricsValidation = true,
                    };
                    response.CreateToolError("tool failed", call);
                    return Task.FromResult(response);
                },
                tools: ("gh_get", ReadOnlySchema, false));

            var responseObject = JObject.Parse((await dispatcher.DispatchAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":22,\"method\":\"tools/call\",\"params\":{\"name\":\"gh_get\",\"arguments\":{}}}",
                cancellation.Token))!);

            Assert.Equal(cancellation.Token, observed);
            Assert.Equal(true, (bool?)responseObject["result"]?["isError"]);
            Assert.Null(responseObject["error"]);
            Assert.Contains("tool failed", (string?)responseObject["result"]?["content"]?[0]?["text"]);
        }

        [Fact]
        public void StaticProviders_ObserveCancellation()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var resourceProvider = new StaticMcpResourceProvider();
            var promptProvider = new StaticMcpPromptProvider();

            Assert.ThrowsAny<System.OperationCanceledException>(() =>
                resourceProvider.ListResourcesAsync(cancellation.Token).GetAwaiter().GetResult());
            Assert.ThrowsAny<System.OperationCanceledException>(() =>
                promptProvider.ListPromptsAsync(cancellation.Token).GetAwaiter().GetResult());
        }

        private static JsonRpcDispatcher BuildDispatcher(
            McpServerOptions options,
            params (string name, string schema, bool mutatesCanvas)[] tools)
        {
            return BuildDispatcher(options, _ => Task.FromResult(new AIReturn()), tools);
        }

        private static JsonRpcDispatcher BuildDispatcher(
            McpServerOptions options,
            System.Func<AIToolCall, Task<AIReturn>> executor,
            params (string name, string schema, bool mutatesCanvas)[] tools)
        {
            var catalog = new Dictionary<string, AITool>();
            foreach (var (name, schema, mutatesCanvas) in tools)
            {
                catalog[name] = new AITool(
                    name: name,
                    description: $"Test tool {name}",
                    category: "Test",
                    parametersSchema: schema,
                    execute: _ => Task.FromResult(new AIReturn()),
                    mutatesCanvas: mutatesCanvas);
            }

            var adapter = new AIToolMcpAdapter(options, () => catalog, executor);
            return new JsonRpcDispatcher(options, adapter);
        }

        private static JsonRpcDispatcher BuildDispatcher(
            McpServerOptions options,
            IMcpResourceProvider resourceProvider,
            IMcpPromptProvider promptProvider)
        {
            var adapter = new AIToolMcpAdapter(
                options,
                () => new Dictionary<string, AITool>(),
                _ => Task.FromResult(new AIReturn()));
            return new JsonRpcDispatcher(options, adapter, resourceProvider, promptProvider);
        }
    }
}
