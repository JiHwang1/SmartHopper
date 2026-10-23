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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SmartHopper.Infrastructure.Mcp
{
    /// <summary>Serves reusable prompt templates embedded in the Infrastructure assembly.</summary>
    public sealed class StaticMcpPromptProvider : IMcpPromptProvider
    {
        private static readonly IReadOnlyList<(string Name, string Description, string FileName)> Prompts =
            new List<(string Name, string Description, string FileName)>
            {
                ("grasshopper-expert", "A general-purpose Grasshopper and SmartHopper expert.", "grasshopper-expert.md"),
                ("script-writer", "Helps create, review, and edit Grasshopper script components.", "script-writer.md"),
                ("canvas-debugger", "Diagnoses canvas issues and suggests debugging steps.", "canvas-debugger.md"),
                ("definition-builder", "Designs and builds robust Grasshopper component workflows.", "definition-builder.md"),
                ("performance-reviewer", "Reviews Grasshopper definitions for measurable performance problems.", "performance-reviewer.md"),
            };

        public Task<IReadOnlyList<McpPrompt>> ListPromptsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prompts = Prompts
                .Select(prompt => new McpPrompt(
                    new Uri($"prompts:///{prompt.Name}", UriKind.Absolute),
                    prompt.Name,
                    prompt.Description,
                    token => this.GetPromptMessagesAsync(prompt.FileName, token)))
                .ToList();

            return Task.FromResult<IReadOnlyList<McpPrompt>>(prompts);
        }

        public async Task<McpPrompt?> GetPromptAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (uri == null)
            {
                return null;
            }

            var absolute = uri.IsAbsoluteUri ? uri : new Uri($"prompts://{uri}", UriKind.Absolute);
            var prompts = await this.ListPromptsAsync(cancellationToken).ConfigureAwait(false);
            return prompts.FirstOrDefault(prompt =>
                string.Equals(prompt.Uri.ToString(), absolute.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        private Task<IReadOnlyList<McpPromptMessage>> GetPromptMessagesAsync(
            string fileName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = EmbeddedMcpResourceLoader.ReadMarkdown(fileName);
            return Task.FromResult<IReadOnlyList<McpPromptMessage>>(new List<McpPromptMessage>
            {
                new McpPromptMessage("user", "text", text),
            });
        }
    }
}
