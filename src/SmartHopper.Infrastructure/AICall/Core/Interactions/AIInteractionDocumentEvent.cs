using System;
using SmartHopper.Infrastructure.AICall.Core.Base;

namespace SmartHopper.Infrastructure.AICall.Core.Interactions
{
    public enum AIDocumentEventKind { ActiveDocument, SwitchedDocument, CanvasChanged }

    /// <summary>A user-side observation, encoded as ordinary text by existing providers.</summary>
    public sealed class AIInteractionDocumentEvent : AIInteractionText, IAIRenderInteraction
    {
        public AIInteractionDocumentEvent() { this.Agent = AIAgent.User; this.TurnId = Guid.NewGuid().ToString("N"); }
        public AIDocumentEventKind Kind { get; set; }
        public string? DocumentId { get; set; }
        public string? DocumentName { get; set; }
        string IAIRenderInteraction.GetRoleClassForRender() => "system";
        string IAIRenderInteraction.GetDisplayNameForRender() => "Canvas";
        string IAIRenderInteraction.GetRawContentForRender() => this.Content ?? string.Empty;
        string IAIRenderInteraction.GetRawReasoningForRender() => string.Empty;
    }
}
