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
using System.IO;
using System.Threading.Tasks;
using Grasshopper.Kernel;
using Newtonsoft.Json.Linq;
using SmartHopper.Core.Grasshopper.Utils.Canvas;
using SmartHopper.Infrastructure.AICall.Core.Returns;
using SmartHopper.Infrastructure.AICall.Tools;
using SmartHopper.Infrastructure.AITools;

namespace SmartHopper.Core.Grasshopper.AITools
{
    /// <summary>Tool provider for saving the active or an explicitly selected open document.</summary>
    public class gh_document_save : IAIToolProvider
    {
        private readonly string toolName = "gh_document_save";

        /// <inheritdoc/>
        public IEnumerable<AITool> GetTools()
        {
            yield return new AITool(
                name: this.toolName,
                description: "Save an open Grasshopper document. documentId selects a document by the id returned from gh_document_list; omit it to use the active document. If filePath is omitted, save to the document's current path.",
                category: "Document",
                parametersSchema: @"{ ""type"": ""object"", ""properties"": { ""documentId"": { ""type"": ""string"", ""description"": ""Optional open-document id. Defaults to the active document."" }, ""filePath"": { ""type"": ""string"", ""description"": ""Optional absolute .gh or .ghx destination path. Defaults to the document's current path."" } } }",
                execute: this.GhDocumentSaveToolAsync,
                mutatesCanvas: true,
                tags: new[] { "document", "save", "file" },
                outputSchema: GhDocumentTools.SaveOutputSchema,
                annotations: new AIToolAnnotations(readOnlyHint: false, destructiveHint: true),
                surfaces: GhDocumentTools.InteractiveSurfaces);
        }

        private Task<AIReturn> GhDocumentSaveToolAsync(AIToolCall toolCall)
        {
            return GhDocumentTools.RunAsync(toolCall, args =>
            {
                var documentId = args["documentId"]?.ToString();
                var requestedPath = args["filePath"]?.ToString();
                string? targetPath = null;
                if (!string.IsNullOrWhiteSpace(requestedPath) &&
                    !GrasshopperFilePath.TryNormalize(requestedPath, mustExist: false, out var normalizedPath, out var pathError))
                {
                    throw new ArgumentException(pathError);
                }
                else if (!string.IsNullOrWhiteSpace(requestedPath))
                {
                    targetPath = Path.GetFullPath(requestedPath);
                }

                GH_Document document;
                if (string.IsNullOrWhiteSpace(documentId))
                {
                    document = CanvasAccess.GetCurrentCanvas()
                        ?? throw new InvalidOperationException("No active Grasshopper document.");
                }
                else
                {
                    document = GhDocumentTools.RequireDocument(documentId);
                }

                targetPath = string.IsNullOrWhiteSpace(targetPath) ? document.FilePath : targetPath;
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    throw new InvalidOperationException("No file path is available. Provide an absolute filePath.");
                }

                var save = GhDocumentAccess.Save(document, targetPath, toolCall.CancellationToken);
                if (save.Rejected)
                {
                    var rejected = new JObject
                    {
                        ["document"] = null,
                        ["saved"] = false,
                        ["rejected"] = true,
                        ["filePath"] = targetPath,
                    };
                    rejected.WithEnvelope(ToolResultEnvelope.Create(this.toolName, ToolResultContentType.Object, payloadPath: "document"));
                    return Task.FromResult(rejected);
                }

                if (!save.Saved)
                {
                    throw new InvalidOperationException($"Could not save the Grasshopper document to {targetPath}.");
                }

                var payload = new JObject
                {
                    ["document"] = GhDocumentAccess.Describe(document, GhDocumentAccess.GetActiveDocument()),
                    ["saved"] = true,
                    ["rejected"] = false,
                    ["filePath"] = targetPath,
                };
                payload.WithEnvelope(ToolResultEnvelope.Create(this.toolName, ToolResultContentType.Object, payloadPath: "document"));
                return Task.FromResult(payload);
            });
        }
    }
}
