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
    /// <summary>Serves only Markdown resources embedded in the Infrastructure assembly.</summary>
    public sealed class StaticMcpResourceProvider : IMcpResourceProvider
    {
        private const string DocsScheme = "docs";

        public Task<IReadOnlyList<McpResource>> ListResourcesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resources = AgentKnowledgeCatalog.ListDocuments()
                .Select(document => new McpResource(
                    new Uri($"{DocsScheme}:///{document.Id}", UriKind.Absolute),
                    document.Title,
                    "text/markdown",
                    document.Description,
                    token =>
                    {
                        token.ThrowIfCancellationRequested();
                        return Task.FromResult(AgentKnowledgeCatalog.GetDocumentText(document.Id) ?? string.Empty);
                    }))
                .ToList();

            return Task.FromResult<IReadOnlyList<McpResource>>(resources);
        }

        public async Task<McpResource?> GetResourceAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (uri == null)
            {
                return null;
            }

            var absolute = uri.IsAbsoluteUri ? uri : new Uri($"{DocsScheme}://{uri}", UriKind.Absolute);
            var resources = await this.ListResourcesAsync(cancellationToken).ConfigureAwait(false);
            return resources.FirstOrDefault(resource =>
                string.Equals(resource.Uri.ToString(), absolute.ToString(), StringComparison.OrdinalIgnoreCase));
        }
    }
}
