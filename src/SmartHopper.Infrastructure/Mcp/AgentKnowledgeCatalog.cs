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
using System.Collections.ObjectModel;
using System.Linq;

namespace SmartHopper.Infrastructure.Mcp
{
    /// <summary>Provides the embedded public knowledge used by MCP clients.</summary>
    public static class AgentKnowledgeCatalog
    {
        private static readonly IReadOnlyDictionary<string, AgentKnowledgeDocument> Documents =
            new ReadOnlyDictionary<string, AgentKnowledgeDocument>(
                new Dictionary<string, AgentKnowledgeDocument>(StringComparer.OrdinalIgnoreCase)
                {
                    ["assistant-core"] = new ("assistant-core", "Assistant Core", "Internal agent instruction composition.", "assistant-core.md", false),
                    ["grasshopper-foundations"] = new ("grasshopper-foundations", "Grasshopper Foundations", "Core Grasshopper concepts and solution behavior.", "grasshopper-foundations.md"),
                    ["grasshopper-data-trees"] = new ("grasshopper-data-trees", "Grasshopper Data Trees", "Items, lists, trees, and data matching.", "grasshopper-data-trees.md"),
                    ["grasshopper-definition-design"] = new ("grasshopper-definition-design", "Grasshopper Definition Design", "Robust and maintainable definition practices.", "grasshopper-definition-design.md"),
                    ["grasshopper-geometry"] = new ("grasshopper-geometry", "Grasshopper Geometry", "Units, tolerances, topology, and geometry guidance.", "grasshopper-geometry.md"),
                    ["grasshopper-debugging"] = new ("grasshopper-debugging", "Grasshopper Debugging", "A deterministic process for diagnosing failures.", "grasshopper-debugging.md"),
                    ["grasshopper-performance"] = new ("grasshopper-performance", "Grasshopper Performance", "Profiling and recomputation guidance.", "grasshopper-performance.md"),
                    ["grasshopper-scripting-csharp"] = new ("grasshopper-scripting-csharp", "Grasshopper C# Scripting", "Rhino 8 C# script-component guidance.", "grasshopper-scripting-csharp.md"),
                    ["grasshopper-scripting-python"] = new ("grasshopper-scripting-python", "Grasshopper Python Scripting", "Rhino 8 Python script-component guidance.", "grasshopper-scripting-python.md"),
                    ["grasshopper-scripting-vb"] = new ("grasshopper-scripting-vb", "Grasshopper VB Scripting", "VB script-component guidance.", "grasshopper-scripting-vb.md"),
                    ["smarthopper-tool-strategy"] = new ("smarthopper-tool-strategy", "SmartHopper Tool Strategy", "Tool selection, inspection, and verification guidance.", "smarthopper-tool-strategy.md"),
                    ["smarthopper-providers"] = new ("smarthopper-providers", "SmartHopper Providers", "Provider and model guidance.", "smarthopper-providers.md"),
                    ["smarthopper-research"] = new ("smarthopper-research", "SmartHopper Research", "Research workflow guidance.", "smarthopper-research.md"),
                    ["smarthopper-ghjson"] = new ("smarthopper-ghjson", "SmartHopper GhJSON Guidance", "Guidance for GhJSON and GhPatch.", "smarthopper-ghjson.md"),
                    ["source-policy"] = new ("source-policy", "Knowledge Source Policy", "Source priority and evidence rules.", "source-policy.md"),
                    ["smarthopper-readme"] = new ("smarthopper-readme", "SmartHopper README", "Operational index for SmartHopper tools.", "smarthopper-readme.md"),
                    ["smarthopper-workflows"] = new ("smarthopper-workflows", "SmartHopper Workflows", "Canonical tool chains for common tasks.", "smarthopper-workflows.md"),
                });

        public static IReadOnlyList<AgentKnowledgeDocument> ListDocuments(bool includeInternal = false)
        {
            return Documents.Values
                .Where(document => includeInternal || document.ExposeAsResource)
                .OrderBy(document => document.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string? GetDocumentText(string id)
        {
            return TryGetDocument(id, out var document)
                ? EmbeddedMcpResourceLoader.ReadMarkdown(document.FileName)
                : null;
        }

        public static bool TryGetDocument(string id, out AgentKnowledgeDocument document)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                document = null!;
                return false;
            }

            return Documents.TryGetValue(id.Trim(), out document!);
        }
    }
}
