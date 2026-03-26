namespace TunnelServer.Models;

public enum TunnelStatus
{
    Waiting,
    Connected,
    Closed
}

/// <summary>
/// Represents a logical tunnel owned by a connected client.
/// </summary>
public sealed class Tunnel
{
    public string TunnelId { get; init; } = default!;
    public string? ClientId { get; set; }
    public int PublicPort { get; set; }
    public DateTime CreatedAt { get; init; }
    public TunnelStatus Status { get; set; }
}
