using System;

namespace SmartHopper.Infrastructure.AITools
{
    /// <summary>Identifies the host surfaces on which a tool may be called.</summary>
    [Flags]
    public enum AIToolSurface
    {
        None = 0,
        Chat = 1,
        Direct = 2,
        Mcp = 4,
        Batch = 8,
        All = Chat | Direct | Mcp | Batch,
    }
}
