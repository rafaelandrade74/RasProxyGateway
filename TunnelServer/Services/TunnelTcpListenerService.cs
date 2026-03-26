using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace TunnelServer.Services;

/// <summary>
/// Opens a TCP listener per tunnel and bridges data to the owning WebSocket client.
/// </summary>
public sealed class TunnelTcpListenerService
{
    private readonly ILogger<TunnelTcpListenerService> _logger;

    private readonly ConcurrentDictionary<string, TcpListener> _listenersByTunnel = new();
    private readonly ConcurrentDictionary<string, (TcpClient Client, string TunnelId, string RemoteEndpoint)> _connections = new();

    public TunnelTcpListenerService(ILogger<TunnelTcpListenerService> logger)
    {
        _logger = logger;
    }

    public async Task<int> StartForTunnelAsync(string tunnelId, Func<string, byte[], Task> onDataAsync, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Any, 0); // 0 = porta dinâmica livre
        listener.Start();

        if (!_listenersByTunnel.TryAdd(tunnelId, listener))
        {
            listener.Stop();
            throw new InvalidOperationException($"Listener already exists for tunnel {tunnelId}");
        }

        _logger.LogInformation("TCP listener for tunnel {TunnelId} started on {Endpoint}", tunnelId, listener.LocalEndpoint);

        _ = Task.Run(() => AcceptLoopAsync(listener, tunnelId, onDataAsync, cancellationToken), cancellationToken);

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return port;
    }

    private async Task AcceptLoopAsync(TcpListener listener, string tunnelId, Func<string, byte[], Task> onDataAsync, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                var connectionId = Guid.NewGuid().ToString("N");
                var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                _connections[connectionId] = (client, tunnelId, remote);
                _logger.LogInformation("Incoming connection via tunnel {TunnelId} from {Remote} (ConnectionId {ConnectionId})", tunnelId, remote, connectionId);
                _ = Task.Run(() => HandleTcpClientAsync(connectionId, client, onDataAsync, cancellationToken), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in accept loop for tunnel {TunnelId}", tunnelId);
        }
    }

    private async Task HandleTcpClientAsync(string connectionId, TcpClient client, Func<string, byte[], Task> onDataAsync, CancellationToken cancellationToken)
    {
        using var stream = client.GetStream();
        var buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                var data = buffer.AsSpan(0, read).ToArray();
                await onDataAsync(connectionId, data);
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading TCP connection {ConnectionId}", connectionId);
        }
        finally
        {
            CloseConnection(connectionId);
        }
    }

    public async Task SendToConnectionAsync(string connectionId, byte[] data, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(connectionId, out var entry))
        {
            _logger.LogWarning("Connection {ConnectionId} not found to send data", connectionId);
            return;
        }

        try
        {
            var stream = entry.Client.GetStream();
            await stream.WriteAsync(data.AsMemory(0, data.Length), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing to TCP connection {ConnectionId}", connectionId);
            CloseConnection(connectionId);
        }
    }

    public void StopForTunnel(string tunnelId)
    {
        if (_listenersByTunnel.TryRemove(tunnelId, out var listener))
        {
            listener.Stop();
            _logger.LogInformation("Stopped TCP listener for tunnel {TunnelId}", tunnelId);
        }

        foreach (var kvp in _connections.Where(kvp => kvp.Value.TunnelId == tunnelId).ToArray())
        {
            CloseConnection(kvp.Key);
        }
    }

    private void CloseConnection(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var entry))
        {
            try
            {
                entry.Client.Close();
            }
            catch
            {
                // ignore
            }
            _logger.LogInformation("Closed TCP connection {ConnectionId} for tunnel {TunnelId} from {Remote}", connectionId, entry.TunnelId, entry.RemoteEndpoint);
        }
    }

    public string? GetRemoteEndpoint(string connectionId)
    {
        return _connections.TryGetValue(connectionId, out var entry)
            ? entry.RemoteEndpoint
            : null;
    }
}
