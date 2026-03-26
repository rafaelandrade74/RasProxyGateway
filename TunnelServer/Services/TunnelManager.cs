using System.Collections.Concurrent;
using System.Security.Cryptography;
using TunnelServer.Models;

namespace TunnelServer.Services;

/// <summary>
/// In-memory storage for connected clients and active tunnels.
/// </summary>
public sealed class TunnelManager
{
    private readonly ConcurrentDictionary<string, ConnectedClient> _clients = new();
    private readonly ConcurrentDictionary<string, Tunnel> _tunnels = new(StringComparer.OrdinalIgnoreCase);

    public int ClientCount => _clients.Count;
    public int TunnelCount => _tunnels.Count;

    public bool AddClient(ConnectedClient client) => _clients.TryAdd(client.ClientId, client);

    public ConnectedClient? GetClient(string clientId)
    {
        _clients.TryGetValue(clientId, out var client);
        return client;
    }

    public bool RemoveClient(string clientId) => _clients.TryRemove(clientId, out _);

    public Tunnel? GetTunnelByHost(string host)
    {
        _tunnels.TryGetValue(host, out var tunnel);
        return tunnel;
    }

    public Tunnel? GetTunnelById(string tunnelId)
    {
        _tunnels.TryGetValue(tunnelId, out var tunnel);
        return tunnel;
    }

    public Tunnel CreateTunnel()
    {
        var tunnelId = Guid.NewGuid().ToString("N");
        var tunnel = new Tunnel
        {
            TunnelId = tunnelId,
            ClientId = null,
            PublicPort = 0,
            CreatedAt = DateTime.UtcNow,
            Status = TunnelStatus.Waiting
        };

        _tunnels[tunnelId] = tunnel;
        return tunnel;
    }

    public Tunnel? GetWaitingTunnelByToken(string token)
    {
        foreach (var tunnel in _tunnels.Values)
        {
            if (tunnel.Status == TunnelStatus.Waiting &&
                string.Equals(tunnel.TunnelId, token, StringComparison.OrdinalIgnoreCase))
            {
                return tunnel;
            }
        }

        return null;
    }

    public IEnumerable<Tunnel> RemoveTunnelsByClient(string clientId)
    {
        var removed = new List<Tunnel>();

        foreach (var pair in _tunnels.ToArray())
        {
            if (!string.Equals(pair.Value.ClientId, clientId, StringComparison.Ordinal))
            {
                continue;
            }

            if (_tunnels.TryRemove(pair.Key, out var tunnel))
            {
                removed.Add(tunnel);
            }
        }

        return removed;
    }

    // No DNS/subdomain generation; TunnelId is the identifier used by clients.
}
