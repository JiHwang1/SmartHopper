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
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GhJSON.Grasshopper;
using Grasshopper;
using Grasshopper.Kernel;
using Rhino;

namespace SmartHopper.Core.Grasshopper.Utils.Canvas
{
    /// <summary>
    /// Utility helpers for interacting with the active Grasshopper canvas and document objects.
    /// </summary>
    public static class CanvasAccess
    {
        /// <summary>
        /// Gets the current active Grasshopper document from the canvas.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1024:Use properties where appropriate", Justification = "Ambient UI state access; method communicates non-field-like behavior")]
        public static GH_Document GetCurrentCanvas()
        {
            GH_Document doc = null;
            try
            {
                doc = GhJsonGrasshopper.GetActiveDocument();
            }
            catch
            {
                doc = null;
            }

            return doc;
        }

        /// <summary>
        /// Gets the list of active objects in the current document.
        /// </summary>
        public static List<IGH_ActiveObject> GetCurrentObjects()
        {
            GH_Document doc = GetCurrentCanvas();
            if (doc == null)
            {
                return new List<IGH_ActiveObject>();
            }

            try
            {
                return doc.ActiveObjects() ?? new List<IGH_ActiveObject>();
            }
            catch
            {
                return new List<IGH_ActiveObject>();
            }
        }

        /// <summary>
        /// Finds a document object instance by its GUID.
        /// </summary>
        /// <param name="guid">The instance GUID.</param>
        /// <returns>The matching document object or null if not found.</returns>
        public static IGH_DocumentObject FindInstance(Guid guid)
        {
            var doc = GetCurrentCanvas();
            if (doc == null)
            {
                return null;
            }

            IGH_DocumentObject obj;
            try
            {
                obj = GhJsonGrasshopper.FindObject(doc, guid);
            }
            catch
            {
                return null;
            }

            if (obj is IGH_Component component)
            {
                // Debug.WriteLine("The object is an IGH_Component.");
                return component;
            }
            else if (obj is IGH_Param param)
            {
                // Debug.WriteLine("The object is an IGH_Param.");
                return param;
            }
            else
            {
                // Debug.WriteLine("The object is neither an IGH_Component nor an IGH_Param.");
                return obj;
            }
        }

        /// <summary>
        /// Moves an existing instance by setting its Pivot position by GUID.
        /// </summary>
        /// <param name="guid">The GUID of the instance to move.</param>
        /// <param name="position">The new pivot position, absolute or relative.</param>
        /// <param name="relative">True to interpret position as a relative offset; false for absolute.</param>
        /// <param name="redraw">True to redraw canvas after moving.</param>
        /// <returns>True if the instance was found and moved; otherwise false.</returns>
        public static bool MoveInstance(Guid guid, PointF position, bool relative = false, bool redraw = true)
        {
            // Keep the public synchronous entry point, with the same completion and Undo
            // semantics as batch moves. On the UI thread the async method completes inline.
            return MoveInstancesAsync(new Dictionary<Guid, PointF> { [guid] = position }, relative, redraw)
                .GetAwaiter().GetResult().Contains(guid);
        }

        /// <summary>Compatibility entry point for synchronous batch moves.</summary>
        public static List<Guid> MoveInstance(IDictionary<Guid, PointF> targets, bool relative = false, bool redraw = true)
        {
            return MoveInstancesAsync(targets, relative, redraw).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Moves instances to specific targets by GUID mapping, with optional relative offsets, batching into one undo event.
        /// </summary>
        /// <param name="targets">A mapping of instance GUIDs to target positions.</param>
        /// <param name="relative">If true, interprets target positions as offsets from current positions.</param>
        /// <param name="redraw">True to redraw the canvas after moving objects.</param>
        /// <returns>A task containing the GUIDs that were successfully moved after the final positions and undo record are applied.</returns>
        public static Task<List<Guid>> MoveInstancesAsync(IDictionary<Guid, PointF> targets, bool relative = false, bool redraw = true)
        {
            return MoveInstancesAsync(targets, relative, redraw, CancellationToken.None, GetCurrentCanvas());
        }

        /// <summary>Moves only within the captured document, checking cancellation before the UI mutation.</summary>
        public static Task<List<Guid>> MoveInstancesAsync(IDictionary<Guid, PointF> targets, bool relative, bool redraw, CancellationToken cancellationToken, GH_Document document)
        {
            if (targets == null) throw new ArgumentNullException(nameof(targets));
            var requested = targets.ToArray();
            var completion = new TaskCompletionSource<List<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
            void ApplyMoves()
            {
                try
                {
                    EnsureActiveDocument(document, cancellationToken);
                    var objects = document.Objects.ToDictionary(item => item.InstanceGuid);
                    var moves = new List<(IGH_DocumentObject obj, PointF target)>();
                    var moved = new List<Guid>();
                    foreach (var kvp in requested)
                    {
                        if (!objects.TryGetValue(kvp.Key, out var obj)) continue;
                        var start = obj.Attributes.Pivot;
                        var target = relative ? new PointF(start.X + kvp.Value.X, start.Y + kvp.Value.Y) : kvp.Value;
                        if (start == target) continue;
                        moves.Add((obj, target));
                        moved.Add(kvp.Key);
                    }

                    if (moves.Count > 0)
                    {
                        var undo = document.UndoUtil.CreateGenericObjectEvent("[SH] Move Instances", moves[0].obj);
                        foreach (var move in moves.Skip(1)) move.obj.RecordUndoEvent(undo);
                        foreach (var move in moves)
                        {
                            move.obj.Attributes.Pivot = move.target;
                            move.obj.Attributes.ExpireLayout();
                        }

                        document.UndoUtil.RecordEvent(undo);
                        if (redraw) Instances.RedrawCanvas();
                    }

                    completion.TrySetResult(moved);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }

            if (RhinoApp.InvokeRequired) RhinoApp.InvokeOnUiThread((Action)ApplyMoves);
            else ApplyMoves();
            return completion.Task;
        }

        /// <summary>Rejects a closed/switched document and cancellation before a queued Canvas mutation.</summary>
        internal static void EnsureActiveDocument(GH_Document document, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document == null || !ReferenceEquals(Instances.ActiveCanvas?.Document, document) ||
                !Instances.DocumentServer.Any(item => ReferenceEquals(item, document)))
            {
                throw new InvalidOperationException("The active Grasshopper document changed before the Canvas operation could execute.");
            }
        }

        /// <summary>Starts a follow-up tool on the UI thread only if its original document is still active.</summary>
        internal static Task<T> InvokeInDocumentAsync<T>(GH_Document document, CancellationToken cancellationToken, Func<Task<T>> action)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            async void Run()
            {
                try
                {
                    EnsureActiveDocument(document, cancellationToken);
                    completion.TrySetResult(await action().ConfigureAwait(true));
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }

            if (RhinoApp.InvokeRequired) RhinoApp.InvokeOnUiThread((Action)Run);
            else Run();
            return completion.Task;
        }

        /// <summary>
        /// Removes the document objects with the specified instance GUIDs and records
        /// a single undo event so the operation can be reversed with Ctrl+Z.
        /// </summary>
        /// <param name="guids">The GUIDs of the objects to remove.</param>
        /// <returns>The list of GUIDs that were successfully removed.</returns>
        public static List<Guid> RemoveInstances(IEnumerable<Guid> guids)
        {
            var result = GhJsonGrasshopper.Delete(guids);
            return result.Deleted;
        }

        /// <summary>
        /// Saves the current Grasshopper document to the specified file path.
        /// If no file path is provided, the document is saved to its current location.
        /// Returns the path to which the document was saved, or null if saving failed.
        /// </summary>
        /// <param name="filePath">Optional destination file path. If null, the document's current file path is used.</param>
        /// <returns>The file path where the document was saved, or null.</returns>
        public static string SaveDocument(string filePath = null)
        {
            var doc = GetCurrentCanvas();
            if (doc == null)
            {
                return null;
            }

            var targetPath = filePath;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                targetPath = doc.FilePath;
            }

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return null;
            }

            try
            {
                var writer = new GH_DocumentIO(doc);
                if (writer.SaveQuiet(targetPath))
                {
                    return targetPath;
                }

                Debug.WriteLine("[CanvasAccess] SaveDocument returned false.");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CanvasAccess] SaveDocument failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Pans the canvas view to position a component at a specified horizontal location.
        /// If the component is already within the central 2/3 of the viewport, no panning occurs.
        /// </summary>
        /// <param name="instanceGuid">The GUID of the component to focus on.</param>
        /// <param name="horizontalPosition">
        /// Horizontal position in the viewport where the component should be placed.
        /// 0 = left edge, 0.5 = center, 1 = right edge. Default is 0.5 (centered).
        /// </param>
        /// <returns>True if the canvas was panned, false if no panning was needed or an error occurred.</returns>
        public static bool CenterViewOnComponent(Guid instanceGuid, float horizontalPosition = 0.5f)
        {
            if (instanceGuid == Guid.Empty)
            {
                return false;
            }

            // Clamp horizontal position to valid range
            horizontalPosition = Math.Max(0f, Math.Min(1f, horizontalPosition));

            try
            {
                var canvas = Instances.ActiveCanvas;
                if (canvas?.Document == null)
                {
                    System.Diagnostics.Debug.WriteLine("[CanvasAccess] CenterViewOnComponent: No active canvas");
                    return false;
                }

                var component = canvas.Document.FindObject(instanceGuid, true);
                if (component == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[CanvasAccess] CenterViewOnComponent: Component not found: {instanceGuid}");
                    return false;
                }

                // Get component bounds in canvas coordinates
                var componentBounds = component.Attributes.Bounds;
                var componentCenter = new PointF(
                    componentBounds.X + (componentBounds.Width / 2),
                    componentBounds.Y + (componentBounds.Height / 2));

                // Get current viewport bounds in canvas coordinates
                var viewportBounds = canvas.Viewport.VisibleRegion;

                // Calculate the central 2/3 region for the "already visible" check
                var centralWidth = viewportBounds.Width * 2f / 3f;
                var centralHeight = viewportBounds.Height * 2f / 3f;
                var centralLeft = viewportBounds.X + ((viewportBounds.Width - centralWidth) / 2f);
                var centralTop = viewportBounds.Y + ((viewportBounds.Height - centralHeight) / 2f);
                var centralRegion = new RectangleF(centralLeft, centralTop, centralWidth, centralHeight);

                // Check if component is already within the central 2/3
                if (centralRegion.Contains(componentCenter))
                {
                    System.Diagnostics.Debug.WriteLine("[CanvasAccess] Component already in central region, no pan needed");
                    return false;
                }

                // Calculate the target X position in the viewport based on horizontalPosition
                // horizontalPosition: 0 = left edge, 0.5 = center, 1 = right edge
                var targetViewportX = viewportBounds.X + (viewportBounds.Width * horizontalPosition);

                // Calculate the offset needed to position the component at the target horizontal position
                // and center it vertically
                var viewportCenterY = viewportBounds.Y + (viewportBounds.Height / 2);

                var offsetX = componentCenter.X - targetViewportX;
                var offsetY = componentCenter.Y - viewportCenterY;

                // Pan the viewport
                var newMidpoint = new PointF(
                    canvas.Viewport.MidPoint.X + offsetX,
                    canvas.Viewport.MidPoint.Y + offsetY);

                canvas.Viewport.MidPoint = newMidpoint;
                canvas.Refresh();

                System.Diagnostics.Debug.WriteLine($"[CanvasAccess] Positioned component {instanceGuid} at horizontal {horizontalPosition:P0}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CanvasAccess] Error centering canvas: {ex.Message}");
                return false;
            }
        }
    }
}
