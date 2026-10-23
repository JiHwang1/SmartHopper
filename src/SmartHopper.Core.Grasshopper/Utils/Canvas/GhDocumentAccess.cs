/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using GhJSON.Core;
using GhJSON.Core.Serialization;
using GhJSON.Grasshopper;
using GhJSON.Grasshopper.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Newtonsoft.Json.Linq;

namespace SmartHopper.Core.Grasshopper.Utils.Canvas
{
    /// <summary>Result of an explicitly confirmed document activation.</summary>
    public readonly struct GhDocumentActivationResult
    {
        internal GhDocumentActivationResult(GH_Document? document, bool rejected)
        {
            this.Document = document;
            this.Rejected = rejected;
        }

        /// <summary>Gets the activated document, or null when the operation failed.</summary>
        public GH_Document? Document { get; }

        /// <summary>Gets whether the user declined the confirmation.</summary>
        public bool Rejected { get; }
    }

    /// <summary>Result of an explicitly confirmed document open.</summary>
    public readonly struct GhDocumentOpenResult
    {
        internal GhDocumentOpenResult(GH_Document? document, bool alreadyOpen, bool rejected)
        {
            this.Document = document;
            this.AlreadyOpen = alreadyOpen;
            this.Rejected = rejected;
        }

        /// <summary>Gets the activated document, or null when the operation failed.</summary>
        public GH_Document? Document { get; }

        /// <summary>Gets whether the file was already open before this request.</summary>
        public bool AlreadyOpen { get; }

        /// <summary>Gets whether the user declined the confirmation.</summary>
        public bool Rejected { get; }
    }

    /// <summary>Result of an explicitly confirmed document save.</summary>
    public readonly struct GhDocumentSaveResult
    {
        internal GhDocumentSaveResult(bool saved, bool rejected)
        {
            this.Saved = saved;
            this.Rejected = rejected;
        }

        /// <summary>Gets whether the document was written to disk.</summary>
        public bool Saved { get; }

        /// <summary>Gets whether the user declined the confirmation.</summary>
        public bool Rejected { get; }
    }

    /// <summary>UI-thread-safe access to documents in the Grasshopper document server.</summary>
    public static class GhDocumentAccess
    {
        /// <summary>Gets the currently active document.</summary>
        public static GH_Document? GetActiveDocument()
        {
            return CanvasAccess.RunOnUiThread(() => Instances.ActiveCanvas?.Document);
        }

        /// <summary>Gets a stable snapshot of the open documents.</summary>
        public static IReadOnlyList<GH_Document> GetOpenDocuments(CancellationToken cancellationToken = default)
        {
            return CanvasAccess.RunOnUiThread<IReadOnlyList<GH_Document>>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = new List<GH_Document>();
                if (Instances.DocumentServer != null)
                {
                    foreach (GH_Document document in Instances.DocumentServer)
                    {
                        if (document != null) result.Add(document);
                    }
                }

                return result;
            }, cancellationToken);
        }

        /// <summary>Finds an open document by its document ID.</summary>
        public static GH_Document? FindById(string? documentId, CancellationToken cancellationToken = default)
        {
            if (!Guid.TryParse(documentId, out var id)) return null;
            return CanvasAccess.RunOnUiThread<GH_Document?>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return FindOpenByIdInline(id);
            }, cancellationToken);
        }

        /// <summary>Returns whether a captured document is still open.</summary>
        public static bool IsOpen(GH_Document? document, CancellationToken cancellationToken = default)
        {
            if (document == null) return false;
            return CanvasAccess.RunOnUiThread(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return IsOpenInline(document);
            }, cancellationToken);
        }

        /// <summary>Gets the display name of a document.</summary>
        public static string GetDisplayName(GH_Document? document)
        {
            return string.IsNullOrWhiteSpace(document?.FilePath)
                ? "Untitled"
                : Path.GetFileName(document.FilePath);
        }

        /// <summary>Creates a document description from a UI-thread snapshot.</summary>
        public static JObject Describe(GH_Document document, GH_Document? activeDocument)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            return new JObject
            {
                ["documentId"] = document.DocumentID.ToString("D"),
                ["name"] = GetDisplayName(document),
                ["filePath"] = string.IsNullOrWhiteSpace(document.FilePath) ? null : document.FilePath,
                ["isActive"] = ReferenceEquals(document, activeDocument),
                ["isModified"] = document.IsModified,
                ["objectCount"] = document.ObjectCount,
            };
        }

        /// <summary>Takes a UI-thread snapshot of every open document and the active ID.</summary>
        public static (JArray Documents, string? ActiveDocumentId) DescribeOpenDocuments(CancellationToken cancellationToken)
        {
            return CanvasAccess.RunOnUiThread(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var active = Instances.ActiveCanvas?.Document;
                var documents = new JArray();
                if (Instances.DocumentServer != null)
                {
                    foreach (GH_Document document in Instances.DocumentServer)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (document != null) documents.Add(Describe(document, active));
                    }
                }

                return (documents, active?.DocumentID.ToString("D"));
            }, cancellationToken);
        }

        /// <summary>Serializes one bounded page from a captured open document on the UI thread.</summary>
        public static JObject ReadPage(GH_Document document, int page, int pageSize, bool includeInternalizedData, CancellationToken cancellationToken)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            return CanvasAccess.RunOnUiThread(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsOpenInline(document)) throw new InvalidOperationException("The selected Grasshopper document is no longer open.");

                var allObjects = document.Objects.ToList();
                var total = allObjects.Count;
                var pageCount = total == 0 ? 0 : (total + pageSize - 1) / pageSize;
                var skip = (int)Math.Min((long)(page - 1) * pageSize, total);
                var pagedObjects = allObjects.Skip(skip).Take(pageSize).ToList();
                var ghjson = GhJson.ToJson(GhJsonGrasshopper.Serialize(pagedObjects, new SerializationOptions
                {
                    IncludeConnections = true,
                    IncludeGroups = false,
                    IncludeInternalizedData = includeInternalizedData,
                    IncludeRuntimeData = false,
                    IncludeRuntimeMessages = false,
                    IncludeMetadata = false,
                    IncludeSelectedState = false,
                }), new WriteOptions { Indented = false });

                return new JObject
                {
                    ["document"] = Describe(document, Instances.ActiveCanvas?.Document),
                    ["ghjson"] = ghjson,
                    ["pagination"] = new JObject
                    {
                        ["page"] = page,
                        ["pageSize"] = pageSize,
                        ["totalComponents"] = total,
                        ["pageCount"] = pageCount,
                        ["returnedComponents"] = pagedObjects.Count,
                    },
                };
            }, cancellationToken);
        }

        /// <summary>Activates a still-open document on the UI thread.</summary>
        public static GhDocumentActivationResult Activate(string? documentId, CancellationToken cancellationToken, Action<GH_Document>? onActivated = null)
        {
            if (!Guid.TryParse(documentId, out var id)) return new GhDocumentActivationResult(null, false);
            return CanvasAccess.RunOnUiThread(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var document = FindOpenByIdInline(id);
                if (document == null) return new GhDocumentActivationResult(null, false);

                var source = Instances.ActiveCanvas?.Document;
                if (!Confirm(
                    "Switch Grasshopper document",
                    $"Switch the active document to '{GetDisplayName(document)}'? Subsequent tools in this chat will use that document.",
                    cancellationToken))
                {
                    return new GhDocumentActivationResult(null, true);
                }

                EnsureSourceDocumentInline(source, cancellationToken);
                if (!IsOpenInline(document) || !ActivateInline(document))
                {
                    return new GhDocumentActivationResult(null, false);
                }

                onActivated?.Invoke(document);
                return new GhDocumentActivationResult(document, false);
            }, cancellationToken);
        }

        /// <summary>Creates and activates a blank document on the UI thread.</summary>
        public static GhDocumentActivationResult CreateBlank(CancellationToken cancellationToken, Action<GH_Document>? onActivated = null)
        {
            return CanvasAccess.RunOnUiThread(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Instances.ActiveCanvas == null || Instances.DocumentServer == null)
                {
                    return new GhDocumentActivationResult(null, false);
                }

                var source = Instances.ActiveCanvas.Document;
                if (!Confirm(
                    "Create Grasshopper document",
                    "Create and activate a new blank Grasshopper document? The active document will change.",
                    cancellationToken))
                {
                    return new GhDocumentActivationResult(null, true);
                }

                EnsureSourceDocumentInline(source, cancellationToken);
                var document = new GH_Document();
                Instances.DocumentServer.AddDocument(document);
                if (!ActivateInline(document))
                {
                    Instances.DocumentServer.RemoveDocument(document);
                    return new GhDocumentActivationResult(null, false);
                }

                onActivated?.Invoke(document);
                return new GhDocumentActivationResult(document, false);
            }, cancellationToken);
        }

        /// <summary>Opens a validated file and activates it on the UI thread.</summary>
        public static GhDocumentOpenResult Open(string fullPath, CancellationToken cancellationToken, Action<GH_Document>? onActivated = null)
        {
            return CanvasAccess.RunOnUiThread(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Instances.ActiveCanvas == null || Instances.DocumentServer == null)
                {
                    return new GhDocumentOpenResult(null, false, false);
                }

                var source = Instances.ActiveCanvas.Document;
                var existing = FindOpenByPathInline(fullPath);
                var message = existing == null
                    ? $"Open '{fullPath}' and make it the active Grasshopper document? This adds a document."
                    : $"Activate the already-open Grasshopper document at '{fullPath}'? Subsequent tools in this chat will use that document.";
                if (!Confirm("Open Grasshopper document", message, cancellationToken))
                {
                    return new GhDocumentOpenResult(null, existing != null, true);
                }

                EnsureSourceDocumentInline(source, cancellationToken);
                if (existing != null)
                {
                    if (!IsOpenInline(existing) || !ActivateInline(existing))
                    {
                        return new GhDocumentOpenResult(null, true, false);
                    }

                    onActivated?.Invoke(existing);
                    return new GhDocumentOpenResult(existing, true, false);
                }

                var io = new GH_DocumentIO();
                if (!io.Open(fullPath) || io.Document == null)
                {
                    return new GhDocumentOpenResult(null, false, false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                Instances.DocumentServer.AddDocument(io.Document);
                if (!ActivateInline(io.Document))
                {
                    Instances.DocumentServer.RemoveDocument(io.Document);
                    return new GhDocumentOpenResult(null, false, false);
                }

                onActivated?.Invoke(io.Document);
                return new GhDocumentOpenResult(io.Document, false, false);
            }, cancellationToken);
        }

        /// <summary>Saves a captured open document after user confirmation and identity revalidation on the UI thread.</summary>
        public static GhDocumentSaveResult Save(GH_Document document, string targetPath, CancellationToken cancellationToken)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("A save path is required.", nameof(targetPath));
            return CanvasAccess.RunOnUiThread(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsOpenInline(document)) return new GhDocumentSaveResult(false, false);

                var confirmation = File.Exists(targetPath)
                    ? $"Overwrite the existing file with '{GetDisplayName(document)}'?\n\n{targetPath}"
                    : $"Save '{GetDisplayName(document)}' to:\n\n{targetPath}";
                if (!Confirm("Save Grasshopper document", confirmation, cancellationToken))
                {
                    return new GhDocumentSaveResult(false, true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!IsOpenInline(document)) return new GhDocumentSaveResult(false, false);
                return new GhDocumentSaveResult(new GH_DocumentIO(document).SaveQuiet(targetPath), false);
            }, cancellationToken);
        }

        private static bool Confirm(string title, string message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = Eto.Forms.MessageBox.Show(
                message,
                title,
                Eto.Forms.MessageBoxButtons.YesNo,
                Eto.Forms.MessageBoxType.Question,
                Eto.Forms.MessageBoxDefaultButton.No);
            cancellationToken.ThrowIfCancellationRequested();
            return decision == Eto.Forms.DialogResult.Yes;
        }

        private static void EnsureSourceDocumentInline(GH_Document? source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var active = Instances.ActiveCanvas?.Document;
            if (source == null)
            {
                if (active == null) return;
                throw new InvalidOperationException("The active Grasshopper document changed while confirmation was open. No document was changed; retry the request.");
            }

            if (!IsOpenInline(source) || !ReferenceEquals(active, source))
            {
                throw new InvalidOperationException("The active Grasshopper document changed while confirmation was open. No document was changed; retry the request.");
            }
        }

        private static GH_Document? FindOpenByIdInline(Guid id)
        {
            return Instances.DocumentServer?.Cast<GH_Document>().FirstOrDefault(document => document.DocumentID == id);
        }

        private static GH_Document? FindOpenByPathInline(string fullPath)
        {
            var path = Path.GetFullPath(fullPath);
            return Instances.DocumentServer?.Cast<GH_Document>().FirstOrDefault(document =>
                !string.IsNullOrWhiteSpace(document.FilePath) &&
                string.Equals(Path.GetFullPath(document.FilePath), path, StringComparison.OrdinalIgnoreCase));
        }

        private static bool ActivateInline(GH_Document document)
        {
            var canvas = Instances.ActiveCanvas;
            if (canvas == null || !IsOpenInline(document)) return false;
            canvas.Document = document;
            Instances.DocumentServer.PromoteDocument(document);
            canvas.Refresh();
            return ReferenceEquals(canvas.Document, document);
        }

        private static bool IsOpenInline(GH_Document document)
        {
            return Instances.DocumentServer != null && Instances.DocumentServer.Cast<GH_Document>().Any(item => ReferenceEquals(item, document));
        }
    }
}
