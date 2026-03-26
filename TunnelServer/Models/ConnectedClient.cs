using System.Net.WebSockets;

namespace TunnelServer.Models;

/// <summary>
/// Represents a connected WebSocket client.
/// </summary>
public sealed class ConnectedClient
{
    public string ClientId { get; init; } = default!;
    public WebSocket Socket { get; init; } = default!;
    public bool IsAuthenticated { get; set; }
    public DateTime ConnectedAt { get; init; }
    public string? RemoteIp { get; init; }
}
