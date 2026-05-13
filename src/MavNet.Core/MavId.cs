namespace MavNet.Core;

/// <summary>(SystemId, ComponentId) — the MAVLink address pair that identifies one device.</summary>
public readonly record struct MavId(byte SystemId, byte ComponentId)
{
    /// <inheritdoc/>
    public override string ToString() => $"sys{SystemId}-comp{ComponentId}";
}
