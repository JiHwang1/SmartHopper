using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;

namespace SmartHopper.Core.Grasshopper.Utils.Canvas
{
    /// <summary>Coalesces records created within one synchronous UI mutation callback.</summary>
    /// <remarks>Never keep this scope alive over an await or an approval dialog.</remarks>
    internal sealed class CanvasUndoBatch : IDisposable
    {
        private readonly GH_Document document;
        private readonly HashSet<Guid> initialRecordIds;

        public CanvasUndoBatch(GH_Document document)
        {
            this.document = document ?? throw new ArgumentNullException(nameof(document));
            this.initialRecordIds = document.UndoServer.UndoGuids.ToHashSet();
        }

        /// <summary>Discards this batch's records only after the caller has fully restored native state.</summary>
        public void DiscardRestoredChanges()
        {
            foreach (var id in this.document.UndoServer.UndoGuids.Where(id => !this.initialRecordIds.Contains(id)).ToArray())
                this.document.UndoServer.RemoveRecord(id);
        }

        public void Dispose()
        {
            var count = this.document.UndoServer.UndoGuids.Count(id => !this.initialRecordIds.Contains(id));
            if (count > 1) this.document.UndoUtil.MergeRecords(count);
        }
    }
}
