using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TunnelServer.Models;
using TunnelServer.Services;
using TunnelShared.Protocol;
using TunnelShared.Serialization;

namespace TunnelServer.WebSockets;

/// <summary>
/// Handles the WebSocket protocol for clients, authentication, and tunnel creation.
/// </summary>
public sealed class WebSocketHandler
{
    private readonly ILogger<WebSocketHandler> _logger;
    private readonly ITokenValidator _tokenValidator;
    private readonly TunnelManager _tunnelManager;
    private readonly TunnelTcpListenerService _tcpService;
    private readonly CancellationToken _appStopping;

    public WebSocketHandler(
        ILogger<WebSocketHandler> logger,
        ITokenValidator tokenValidator,
        TunnelManager tunnelManager,
        TunnelTcpListenerService tcpService,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _tokenValidator = tokenValidator;
        _tunnelManager = tunnelManager;
        _tcpService = tcpService;
        _appStopping = lifetime.ApplicationStopping;
    }

    public async Task HandleAsync(HttpContext context, WebSocket socket, CancellationToken cancellationToken)
    {
        var clientId = Guid.NewGuid().ToString("N");
        var client = new ConnectedClient
        {
            ClientId = clientId,
            Socket = socket,
            IsAuthenticated = false,
            ConnectedAt = DateTime.UtcNow,
            RemoteIp = context.Connection.RemoteIpAddress?.ToString()
        };

        _logger.LogInformation("Client {ClientId} connected from {RemoteIp}", clientId, context.Connection.RemoteIpAddress);

        // vincula o cancelamento da requisição com o desligamento global da aplicação
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _appStopping);
        var linkedToken = linkedCts.Token;

        try
        {
            await ReceiveLoopAsync(client, linkedToken);
        }
        finally
        {
            _tunnelManager.RemoveClient(clientId);
            var removed = _tunnelManager.RemoveTunnelsByClient(clientId).ToArray();
            foreach (var tunnel in removed)
            {
                tunnel.Status = TunnelStatus.Closed;
                _tcpService.StopForTunnel(tunnel.TunnelId);
            }
            if (removed.Length > 0)
            {
                _logger.LogInformation("Removed {Count} tunnels for client {ClientId}", removed.Length, clientId);
            }

            _logger.LogInformation("Client {ClientId} disconnected", clientId);
            await CloseSocketIfNeeded(socket, linkedToken);
        }
    }

    private async Task ReceiveLoopAsync(ConnectedClient client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && client.Socket.State == WebSocketState.Open)
        {
            var payload = await ReceiveStringAsync(client.Socket, cancellationToken);
            if (payload is null)
            {
                break;
            }

            var message = MessageSerializer.Deserialize(payload);
            if (message is null || message.Type == MessageType.Unknown)
            {
                await SendMessageAsync(client.Socket, new ClientMessage { Type = MessageType.Error, Message = "Invalid JSON" }, cancellationToken);
                continue;
            }

            switch (message.Type)
            {
                case MessageType.Auth:
                    await HandleAuthAsync(client, message, cancellationToken);
                    break;
                case MessageType.CreateTunnel:
                    await HandleCreateTunnelAsync(client, cancellationToken);
                    break;
                case MessageType.ConnectTunnel:
                    await HandleConnectTunnelAsync(client, message, cancellationToken);
                    break;
                case MessageType.TcpData:
                    await HandleTcpDataFromClientAsync(client, message, cancellationToken);
                    break;
                default:
                    await SendMessageAsync(client.Socket, new ClientMessage { Type = MessageType.Error, Message = "Unknown message type" }, cancellationToken);
                    break;
            }
        }
    }

    private async Task HandleAuthAsync(ConnectedClient client, ClientMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.Token))
        {
            await SendMessageAsync(client.Socket, new ClientMessage { Type = MessageType.Error, Message = "Missing token" }, cancellationToken);
            return;
        }

        var token = message.Token;
        if (!_tokenValidator.IsValid(token))
        {
            _logger.LogWarning("Client {ClientId} failed authentication", client.ClientId);
            await SendMessageAsync(client.Socket, new ClientMessage { Type = MessageType.Error, Message = "Invalid token" }, cancellationToken);
            await CloseAsync(client.Socket, WebSocketCloseStatus.PolicyViolation, "Invalid token", cancellationToken);
            return;
        }

        client.IsAuthenticated = true;
        _tunnelManager.AddClient(client);
        _logger.LogInformation("Proxy client authenticated: {ClientId} from {RemoteIp}", client.ClientId, client.RemoteIp);

        await SendMessageAsync(client.Socket, new ClientMessage
        {
            Type = MessageType.Authenticated,
            ClientId = client.ClientId
        }, cancellationToken);
    }

    private async Task HandleCreateTunnelAsync(ConnectedClient client, CancellationToken cancellationToken)
    {
        if (!client.IsAuthenticated)
        {
            await SendMessageAsync(client.Socket, new ClientMessage { Type = MessageType.Error, Message = "Not authenticated" }, cancellationToken);
            return;
        }

        var tunnel = _tunnelManager.CreateTunnel();
        _logger.LogInformation("Tunnel created {TunnelId} for client {ClientId} (status {Status})", tunnel.TunnelId, client.ClientId, tunnel.Status);

        await SendMessageAsync(client.Socket, new ClientMessage
        {
            Type = MessageType.TunnelCreated,
            TunnelId = tunnel.TunnelId,
            Status = tunnel.Status.ToString().ToLowerInvariant()
        }, cancellationToken);
    }

    private async Task HandleConnectTunnelAsync(ConnectedClient client, ClientMessage message, CancellationToken cancellationToken)
    {
        if (!client.IsAuthenticated)
        {
            await SendMessageAsync(client.Socket, new ClientMessage { Type = MessageType.Error, Message = "Not authenticated" }, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(message.TunnelId))
        {
            await SendMessageAsync(client.Socket, new ClientMessage { Type = MessageType.Error, Message = "Missing tunnel_id" }, cancellationToken);
            return;
        }

        var tunnelId = message.TunnelId;
        var tunnel = _tunnelManager.GetTunnelById(tunnelId);
        if (tunnel is null)
        {
            await SendMessageAsync(client.Socket, new ClientMessage { Type = MessageType.Error, Message = "Tunnel not found" }, cancellationToken);
            return;
        }

        if (tunnel.Status != TunnelStatus.Waiting)
        {
            await SendMessageAsync(client.Socket, new ClientMessage { Type = MessageType.Error, Message = "Tunnel not waiting" }, cancellationToken);
            return;
        }

        tunnel.ClientId = client.ClientId;
        tunnel.Status = TunnelStatus.Connected;

        tunnel.PublicPort = await _tcpService.StartForTunnelAsync(tunnel.TunnelId, (connectionId, data) =>
            SendTcpDataToClientAsync(client, connectionId, data, cancellationToken), cancellationToken);
        
        _logger.LogInformation("Tunnel connected {TunnelId} (public port {PublicPort}) for client {ClientId}", tunnelId, tunnel.PublicPort, client.ClientId);

        await SendMessageAsync(client.Socket, new ClientMessage
        {
            Type = MessageType.TunnelConnected,
            TunnelId = tunnel.TunnelId,
            PublicPort = tunnel.PublicPort,
            Status = tunnel.Status.ToString().ToLowerInvariant()
        }, cancellationToken);
    }

    private Task SendTcpDataToClientAsync(ConnectedClient client, string connectionId, byte[] data, CancellationToken cancellationToken)
    {
        var base64 = Convert.ToBase64String(data);
        var remote = _tcpService.GetRemoteEndpoint(connectionId);
        return SendMessageAsync(client.Socket, new ClientMessage
        {
            Type = MessageType.TcpData,
            ConnectionId = connectionId,
            Data = base64,
            RemoteIp = remote
        }, cancellationToken);
    }

    private async Task HandleTcpDataFromClientAsync(ConnectedClient client, ClientMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.ConnectionId) || string.IsNullOrWhiteSpace(message.Data))
        {
            await SendMessageAsync(client.Socket, new ClientMessage { Type = MessageType.Error, Message = "Missing connection_id or data" }, cancellationToken);
            return;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(message.Data);
        }
        catch (FormatException)
        {
            await SendMessageAsync(client.Socket, new ClientMessage { Type = MessageType.Error, Message = "Invalid base64 data" }, cancellationToken);
            return;
        }

        await _tcpService.SendToConnectionAsync(message.ConnectionId, bytes, cancellationToken);
    }

    private static async Task<string?> ReceiveStringAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static Task SendMessageAsync(WebSocket socket, ClientMessage message, CancellationToken cancellationToken)
    {
        var json = MessageSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string description, CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            // Usa CancellationToken.None para não ser interrompido por RequestAborted durante o fechamento.
            await socket.CloseAsync(status, description, CancellationToken.None);
        }
    }

    private static Task CloseSocketIfNeeded(WebSocket socket, CancellationToken cancellationToken)
    {
        return socket.State switch
        {
            WebSocketState.Open => CloseAsync(socket, WebSocketCloseStatus.NormalClosure, "Closed", cancellationToken),
            WebSocketState.CloseReceived => CloseAsync(socket, WebSocketCloseStatus.NormalClosure, "Closed", cancellationToken),
            _ => Task.CompletedTask
        };
    }

}
