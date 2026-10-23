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
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, see <https://www.gnu.org/licenses/lgpl-3.0.html>.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SmartHopper.Infrastructure.Mcp
{
    /// <summary>Describes a static MCP prompt.</summary>
    public sealed class McpPrompt
    {
        public McpPrompt(Uri uri, string name, string description, Func<CancellationToken, Task<IReadOnlyList<McpPromptMessage>>> messagesFactory)
        {
            this.Uri = uri ?? throw new ArgumentNullException(nameof(uri));
            this.Name = name ?? throw new ArgumentNullException(nameof(name));
            this.Description = description ?? throw new ArgumentNullException(nameof(description));
            this.MessagesFactory = messagesFactory ?? throw new ArgumentNullException(nameof(messagesFactory));
        }

        public Uri Uri { get; }

        public string Name { get; }

        public string Description { get; }

        private Func<CancellationToken, Task<IReadOnlyList<McpPromptMessage>>> MessagesFactory { get; }

        public Task<IReadOnlyList<McpPromptMessage>> GetMessagesAsync(CancellationToken cancellationToken = default)
            => this.MessagesFactory(cancellationToken);
    }
}
