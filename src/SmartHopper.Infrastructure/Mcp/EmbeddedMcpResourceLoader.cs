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
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace SmartHopper.Infrastructure.Mcp
{
    /// <summary>Reads Markdown files embedded under <c>Resources/Mcp</c>.</summary>
    internal static class EmbeddedMcpResourceLoader
    {
        private const string ResourcePathFragment = "Resources.Mcp";
        private static readonly ConcurrentDictionary<string, Lazy<string>> Cache =
            new ConcurrentDictionary<string, Lazy<string>>(StringComparer.OrdinalIgnoreCase);

        public static string ReadMarkdown(string fileName)
        {
            if (fileName == null)
            {
                throw new ArgumentNullException(nameof(fileName));
            }

            return Cache.GetOrAdd(
                fileName,
                name => new Lazy<string>(() => Load(name), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        private static string Load(string fileName)
        {
            var assembly = typeof(EmbeddedMcpResourceLoader).GetTypeInfo().Assembly;
            var resourceNames = assembly.GetManifestResourceNames();
            var resourceName = resourceNames
                .Where(name => name.IndexOf(ResourcePathFragment, StringComparison.OrdinalIgnoreCase) >= 0
                    && name.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(name => name.Length)
                .FirstOrDefault();

            if (resourceName == null)
            {
                throw new FileNotFoundException(
                    $"Embedded MCP resource not found for '{fileName}'. Available resources: {string.Join(", ", resourceNames)}.");
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                throw new FileNotFoundException($"Embedded MCP resource stream not found: {resourceName}");
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
