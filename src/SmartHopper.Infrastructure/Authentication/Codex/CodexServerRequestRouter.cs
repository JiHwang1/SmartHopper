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
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SmartHopper.Infrastructure.Authentication.Codex
{
    /// <summary>
    /// Shares the existing single-handler transport contract without changing implementations or fakes.
    /// </summary>
    internal sealed class CodexServerRequestRouter
    {
        private static readonly ConditionalWeakTable<ICodexConversationTransport, CodexServerRequestRouter> Routers = new ();
        private readonly ICodexConversationTransport transport;
        private readonly object sync = new ();
        private readonly Dictionary<string, CodexServerRequestHandler> handlers = new (StringComparer.Ordinal);
        private readonly CodexServerRequestHandler dispatch;
        private CodexServerRequestHandler? previousHandler;
        private int owners;

        private CodexServerRequestRouter(ICodexConversationTransport transport)
        {
            this.transport = transport;
            this.dispatch = this.DispatchAsync;
        }

        internal static CodexServerRequestRouter Acquire(ICodexConversationTransport transport)
        {
            var router = Routers.GetValue(transport, value => new CodexServerRequestRouter(value));
            lock (router.sync)
            {
                if (router.owners++ == 0)
                {
                    router.previousHandler = transport.ServerRequestHandler;
                    transport.ServerRequestHandler = router.dispatch;
                }
            }

            return router;
        }

        internal void Register(string threadId, CodexServerRequestHandler handler)
        {
            lock (this.sync)
            {
                // A thread cannot belong to two Canvas sessions, even if a broken transport reuses an ID.
                this.handlers.Add(threadId, handler);
            }
        }

        internal void Release(string? threadId)
        {
            lock (this.sync)
            {
                if (threadId != null)
                {
                    this.handlers.Remove(threadId);
                }

                if (--this.owners == 0)
                {
                    if (this.transport.ServerRequestHandler == this.dispatch)
                    {
                        this.transport.ServerRequestHandler = this.previousHandler;
                    }

                    this.previousHandler = null;
                }
            }
        }

        private Task DispatchAsync(CodexServerRequest request, CancellationToken cancellationToken)
        {
            CodexServerRequestHandler? handler;
            lock (this.sync)
            {
                if (request.Method == "item/tool/call")
                {
                    var thread = request.Parameters["threadId"];
                    handler = thread?.Type == JTokenType.String &&
                        this.handlers.TryGetValue(thread.Value<string>()!, out var registered)
                        ? registered
                        : null;
                }
                else
                {
                    handler = this.previousHandler;
                }
            }

            if (handler != null)
            {
                return handler(request, cancellationToken);
            }

            // Never forward an unknown Canvas thread to a different session or a legacy handler.
            return request.Method == "item/tool/call"
                ? this.transport.SendResponseAsync(
                    request.RequestId,
                    CodexDynamicToolResult.Error("The Canvas conversation is no longer active or is unknown.").ToResponse(),
                    cancellationToken)
                : this.transport.SendErrorAsync(request.RequestId, -32601, "Client method not supported.", cancellationToken);
        }
    }
}
