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

/*
 * JSON-RPC dispatch shape and MCP method names structurally adapted from Cordyceps
 * (https://github.com/brookstalley/cordyceps, McpServer.cs).
 * Copyright (c) 2026 Brooks Talley. Licensed under the MIT License.
 */

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SmartHopper.Infrastructure.Utils;

namespace SmartHopper.Infrastructure.Mcp
{
    /// <summary>
    /// Translates JSON-RPC 2.0 envelopes into MCP method invocations against the
    /// <see cref="AIToolMcpAdapter"/>. Concurrency is serialised by a
    /// <see cref="SemaphoreSlim"/> so multiple HTTP clients cannot race on the
    /// Grasshopper document (see <c>docs/Architecture/mcp-server.md</c> §6).
    /// </summary>
    public sealed class JsonRpcDispatcher : IDisposable
    {
        private const string ProtocolVersion = "2025-03-26";

        // JSON-RPC error codes per https://www.jsonrpc.org/specification.
        private const int ParseError = -32700;
        private const int InvalidRequest = -32600;
        private const int MethodNotFound = -32601;
        private const int InvalidParams = -32602;
        private const int InternalError = -32603;
        private const int ResourceNotFound = -32002;

        private readonly McpServerOptions options;
        private readonly AIToolMcpAdapter adapter;
        private readonly IMcpResourceProvider? resourceProvider;
        private readonly IMcpPromptProvider? promptProvider;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcDispatcher"/> class.
        /// </summary>
        public JsonRpcDispatcher(McpServerOptions options, AIToolMcpAdapter adapter)
            : this(options, adapter, null, null)
        {
        }

        /// <summary>Initializes a dispatcher with optional static resource and prompt providers.</summary>
        public JsonRpcDispatcher(
            McpServerOptions options,
            AIToolMcpAdapter adapter,
            IMcpResourceProvider? resourceProvider,
            IMcpPromptProvider? promptProvider)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            this.resourceProvider = resourceProvider;
            this.promptProvider = promptProvider;
        }

        /// <summary>
        /// Parses a raw JSON-RPC request body and produces a serialized response body.
        /// Returns <c>null</c> for notifications (no <c>id</c> in the request).
        /// </summary>
        /// <param name="requestBody">Raw request payload.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task<string?> DispatchAsync(string requestBody, CancellationToken cancellationToken = default)
        {
            JToken parsed;
            try
            {
                parsed = JToken.Parse(requestBody ?? string.Empty);
            }
            catch (JsonReaderException ex)
            {
                return SerializeError(null, ParseError, $"Parse error: {ex.Message}");
            }

            if (parsed is not JObject request ||
                request["jsonrpc"]?.Type != JTokenType.String ||
                request.Value<string>("jsonrpc") != JsonRpcEnvelope.Version ||
                request["method"]?.Type != JTokenType.String ||
                string.IsNullOrWhiteSpace(request.Value<string>("method")) ||
                request.ContainsKey("result") || request.ContainsKey("error"))
            {
                return SerializeError(null, InvalidRequest, "Invalid JSON-RPC request.");
            }

            var id = request["id"];
            if (id != null && id.Type != JTokenType.String && id.Type != JTokenType.Integer &&
                id.Type != JTokenType.Float && id.Type != JTokenType.Null)
            {
                return SerializeError(null, InvalidRequest, "Invalid JSON-RPC request id.");
            }

            var method = request.Value<string>("method");
            var paramsToken = request["params"] as JObject;
            if (request.ContainsKey("params") && paramsToken == null)
            {
                return id == null ? null : SerializeError(id, InvalidParams, "Parameters must be a JSON object.");
            }

            await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                JToken result;
                switch (method)
                {
                    case "initialize":
                        result = this.HandleInitialize();
                        break;
                    case "notifications/initialized":
                    case "initialized":
                        result = new JObject();
                        break;
                    case "ping":
                        result = new JObject();
                        break;
                    case "tools/list":
                        result = this.HandleToolsList();
                        break;
                    case "tools/call":
                        result = await this.HandleToolsCallAsync(paramsToken, cancellationToken).ConfigureAwait(false);
                        break;
                    case "resources/list":
                        result = await this.HandleResourcesListAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case "resources/read":
                        result = await this.HandleResourcesReadAsync(paramsToken, cancellationToken).ConfigureAwait(false);
                        break;
                    case "prompts/list":
                        result = await this.HandlePromptsListAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case "prompts/get":
                        result = await this.HandlePromptsGetAsync(paramsToken, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        return id == null ? null : SerializeError(id, MethodNotFound, $"Method not found: {method}");
                }

                if (id == null)
                {
                    // Request without id is a notification per JSON-RPC; do not reply.
                    return null;
                }

                return JsonRpcEnvelope.Result(id, result).ToString(Formatting.None);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (McpProtocolException ex)
            {
                return id == null ? null : SerializeError(id, ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                return id == null ? null : SerializeError(id, InternalError, $"Internal error: {ex.Message}");
            }
            finally
            {
                this.gate.Release();
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            this.gate.Dispose();
        }

        private JObject HandleInitialize()
        {
            return new JObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject { ["listChanged"] = false },
                    ["resources"] = new JObject { ["listChanged"] = false },
                    ["prompts"] = new JObject { ["listChanged"] = false },
                },
                ["serverInfo"] = new JObject
                {
                    ["name"] = this.options.ServerName,
                    ["version"] = this.options.ServerVersion ?? GetAssemblyVersion(),
                },
            };
        }

        private JObject HandleToolsList()
        {
            var descriptors = this.adapter.BuildDescriptors();
            var list = new JArray();
            foreach (var d in descriptors)
            {
                list.Add(d.ToMcpJson());
            }

            return new JObject { ["tools"] = list };
        }

        private async Task<JToken> HandleToolsCallAsync(JObject? paramsToken, CancellationToken cancellationToken)
        {
            if (paramsToken == null)
            {
                return BuildToolError("Missing 'params'");
            }

            var toolName = paramsToken.Value<string?>("name");
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return BuildToolError("Missing 'name'");
            }

            var argumentsToken = paramsToken["arguments"];
            JObject? arguments;
            if (argumentsToken is JObject obj)
            {
                arguments = obj;
            }
            else if (argumentsToken == null || argumentsToken.Type == JTokenType.Null)
            {
                arguments = new JObject();
            }
            else
            {
                return BuildToolError("'arguments' must be a JSON object");
            }

            var result = await this.adapter.ExecuteAsync(toolName!, arguments, cancellationToken).ConfigureAwait(false);
            return BuildToolCallEnvelope(result);
        }

        private async Task<JToken> HandleResourcesListAsync(CancellationToken cancellationToken)
        {
            var list = new JArray();
            if (this.resourceProvider != null)
            {
                var resources = await this.resourceProvider.ListResourcesAsync(cancellationToken).ConfigureAwait(false);
                foreach (var resource in resources)
                {
                    list.Add(new JObject
                    {
                        ["uri"] = resource.Uri.ToString(),
                        ["name"] = resource.Name,
                        ["mimeType"] = resource.MimeType,
                        ["description"] = resource.Description ?? string.Empty,
                    });
                }
            }

            return new JObject { ["resources"] = list };
        }

        private async Task<JToken> HandleResourcesReadAsync(JObject? paramsToken, CancellationToken cancellationToken)
        {
            var uriString = paramsToken?["uri"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(uriString) || !Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
            {
                throw new McpProtocolException(InvalidParams, "Missing or invalid 'uri'");
            }

            if (this.resourceProvider == null)
            {
                throw new McpProtocolException(ResourceNotFound, $"Resource not found: {uri}");
            }

            var resource = await this.resourceProvider.GetResourceAsync(uri, cancellationToken).ConfigureAwait(false);
            if (resource == null)
            {
                throw new McpProtocolException(ResourceNotFound, $"Resource not found: {uri}");
            }

            var text = await resource.GetTextAsync(cancellationToken).ConfigureAwait(false);
            return new JObject
            {
                ["contents"] = new JArray
                {
                    new JObject
                    {
                        ["uri"] = resource.Uri.ToString(),
                        ["mimeType"] = resource.MimeType,
                        ["text"] = text ?? string.Empty,
                    },
                },
            };
        }

        private async Task<JToken> HandlePromptsListAsync(CancellationToken cancellationToken)
        {
            var list = new JArray();
            if (this.promptProvider != null)
            {
                var prompts = await this.promptProvider.ListPromptsAsync(cancellationToken).ConfigureAwait(false);
                foreach (var prompt in prompts)
                {
                    list.Add(new JObject
                    {
                        ["name"] = prompt.Name,
                        ["description"] = prompt.Description,
                    });
                }
            }

            return new JObject { ["prompts"] = list };
        }

        private async Task<JToken> HandlePromptsGetAsync(JObject? paramsToken, CancellationToken cancellationToken)
        {
            var name = paramsToken?["name"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new McpProtocolException(InvalidParams, "Missing 'name'");
            }

            if (this.promptProvider == null
                || !Uri.TryCreate($"prompts:///{Uri.EscapeDataString(name)}", UriKind.Absolute, out var uri))
            {
                throw new McpProtocolException(InvalidParams, $"Prompt not found: {name}");
            }

            var prompt = await this.promptProvider.GetPromptAsync(uri, cancellationToken).ConfigureAwait(false);
            if (prompt == null)
            {
                throw new McpProtocolException(InvalidParams, $"Prompt not found: {name}");
            }

            var messages = new JArray();
            foreach (var message in await prompt.GetMessagesAsync(cancellationToken).ConfigureAwait(false))
            {
                messages.Add(new JObject
                {
                    ["role"] = message.Role,
                    ["content"] = new JObject
                    {
                        ["type"] = message.ContentType,
                        ["text"] = message.Text,
                    },
                });
            }

            return new JObject
            {
                ["description"] = prompt.Description,
                ["messages"] = messages,
            };
        }

        private static JObject BuildToolCallEnvelope(McpToolCallResult result)
        {
            var content = new JArray
            {
                new JObject
                {
                    ["type"] = "text",
                    ["text"] = result.Payload.ToString(Formatting.None),
                },
            };

            foreach (var image in result.Images)
            {
                content.Add(new JObject
                {
                    ["type"] = "image",
                    ["data"] = image.ImageData,
                    ["mimeType"] = image.MimeType,
                });
            }

            return new JObject
            {
                ["content"] = content,
                ["isError"] = result.IsError,
            };
        }

        private static JObject BuildToolError(string message)
        {
            var content = new JArray
            {
                new JObject
                {
                    ["type"] = "text",
                    ["text"] = message ?? string.Empty,
                },
            };
            return new JObject
            {
                ["content"] = content,
                ["isError"] = true,
            };
        }

        private static string SerializeError(JToken? id, int code, string message)
        {
            return JsonRpcEnvelope.Error(id, code, message).ToString(Formatting.None);
        }

        private static string GetAssemblyVersion()
        {
            var asm = typeof(JsonRpcDispatcher).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                return info!;
            }

            return asm.GetName().Version?.ToString() ?? "0.0.0";
        }

        private sealed class McpProtocolException : Exception
        {
            public McpProtocolException(int code, string message)
                : base(message)
            {
                this.Code = code;
            }

            public int Code { get; }
        }
    }
}
