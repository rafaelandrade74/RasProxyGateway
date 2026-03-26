using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using TunnelClient.Configuration;
using TunnelShared.Protocol;
using TunnelShared.Serialization;

namespace TunnelClient.Services;

internal sealed class WebSocketTunnelClient
{
    private ClientWebSocket? _socket;
    private readonly ClientOptions _options;
    private readonly CancellationToken _cancellationToken;

    private readonly Dictionary<string, TcpClient> _localConnections = new(StringComparer.Ordinal);
    private readonly object _connectionsLock = new();

    private TaskCompletionSource<string> _authTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<string> _tunnelCreatedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<int> _tunnelConnectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WebSocketTunnelClient(ClientOptions options, CancellationToken cancellationToken)
    {
        _options = options;
        _cancellationToken = cancellationToken;
    }

    public async Task RunAsync()
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            _authTcs.TrySetCanceled();
            _tunnelCreatedTcs.TrySetCanceled();
            _tunnelConnectedTcs.TrySetCanceled();

            var socket = new ClientWebSocket();
            _socket = socket;

            Console.WriteLine($"➡️  Conectando ao servidor {_options.ServerUri} (destino {_options.TargetHost}:{_options.TargetPort})");

            try
            {
                await socket.ConnectAsync(_options.ServerUri, _cancellationToken);
                Console.WriteLine("✅ WebSocket conectado.");
            }
            catch (Exception ex) when (!_cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"❌ Falha ao conectar: {ex.Message}. Tentando novamente em 5s...");
                await Task.Delay(TimeSpan.FromSeconds(5), _cancellationToken);
                continue;
            }

            var receiveTask = Task.Run(() => ReceiveLoopAsync(socket), _cancellationToken);

            // re-cria os TCS para este ciclo
            ResetTcs();

            await SendMessageAsync(socket, new ClientMessage { Type = MessageType.Auth, Token = _options.Token });
            var clientId = await _authTcs.Task;
            Console.WriteLine($"🔐 Autenticado. ClientId = {clientId}");

            await SendMessageAsync(socket, new ClientMessage { Type = MessageType.CreateTunnel });
            var tunnelId = await _tunnelCreatedTcs.Task;
            Console.WriteLine($"🛠️  Tunnel criado aguardando conexão. Id = {tunnelId}");

            await SendMessageAsync(socket, new ClientMessage { Type = MessageType.ConnectTunnel, TunnelId = tunnelId });
            var publicPort = await _tunnelConnectedTcs.Task;
            Console.WriteLine($"🌐 Tunnel conectado. Porta pública: {publicPort}");
            Console.WriteLine("Pronto para encaminhar dados. Ctrl+C para encerrar.");

            await receiveTask;

            if (_cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                socket.Dispose();
            }
            catch { /* ignore */ }

            Console.WriteLine("⚠️  Conexão perdida. Tentando reconectar em 5s...");
            await Task.Delay(TimeSpan.FromSeconds(5), _cancellationToken);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket)
    {
        try
        {
            while (!_cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var payload = await ReceiveStringAsync(socket, _cancellationToken);
                if (payload is null)
                {
                    break;
                }

                HandleMessage(payload);
            }
        }
        catch (OperationCanceledException)
        {
            // graceful exit
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  Erro na recepção do WebSocket: {ex.Message}");
        }
        finally
        {
            await CleanupAsync();
        }
    }

    private void HandleMessage(string json)
    {
        var msg = MessageSerializer.Deserialize(json);
        if (msg is null)
        {
            Console.WriteLine("⚠️  Mensagem sem tipo.");
            return;
        }

        switch (msg.Type)
        {
            case MessageType.TcpData:
                _ = HandleTcpDataAsync(msg);
                return;

            case MessageType.Unknown:
                Console.WriteLine($"ℹ️  Mensagem desconhecida: {json}");
                break;
        }

        switch (msg.Type)
        {
            case MessageType.Authenticated:
                if (!string.IsNullOrWhiteSpace(msg.ClientId))
                {
                    _authTcs.TrySetResult(msg.ClientId);
                }
                break;

            case MessageType.TunnelCreated:
                if (!string.IsNullOrWhiteSpace(msg.TunnelId))
                {
                    _tunnelCreatedTcs.TrySetResult(msg.TunnelId);
                }
                break;

            case MessageType.TunnelConnected:
                if (msg.PublicPort is int port)
                {
                    _tunnelConnectedTcs.TrySetResult(port);
                    Console.WriteLine($"🌐 Tunnel ativo na porta pública {port}");
                }
                break;

            case MessageType.Error:
                Console.WriteLine($"❌ Erro do servidor: {msg.Message ?? "Erro não informado"}");
                break;

            default:
                Console.WriteLine($"ℹ️  Mensagem desconhecida: {msg.Type} - {json}");
                break;
        }
    }

    private void ResetTcs()
    {
        _authTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _tunnelCreatedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _tunnelConnectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task HandleTcpDataAsync(ClientMessage message)
    {
        var connectionId = message.ConnectionId;
        var base64 = message.Data;
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(base64))
        {
            return;
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            Console.WriteLine("⚠️  Dados base64 inválidos recebidos.");
            return;
        }

        var local = await GetOrCreateLocalConnectionAsync(connectionId, message.RemoteIp);
        if (local is null)
        {
            return;
        }

        try
        {
            await local.GetStream().WriteAsync(payload, 0, payload.Length, _cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  Falha ao escrever para destino local: {ex.Message}");
            await CloseLocalAsync(connectionId);
        }
    }

    private async Task<TcpClient?> GetOrCreateLocalConnectionAsync(string connectionId, string? remoteIp)
    {
        TcpClient? client;
        lock (_connectionsLock)
        {
            _localConnections.TryGetValue(connectionId, out client);
        }

        if (client is { Connected: true })
        {
            return client;
        }

        client = new TcpClient();
        try
        {
            await client.ConnectAsync(_options.TargetHost, _options.TargetPort, _cancellationToken);
            Console.WriteLine($"↔️  Nova conexão {connectionId} de {remoteIp ?? "unknown"} para {_options.TargetHost}:{_options.TargetPort}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Não foi possível conectar ao destino local {_options.TargetHost}:{_options.TargetPort}: {ex.Message}");
            return null;
        }

        lock (_connectionsLock)
        {
            _localConnections[connectionId] = client;
        }

        _ = Task.Run(() => PumpLocalToServerAsync(connectionId, client), _cancellationToken);
        return client;
    }

    private async Task PumpLocalToServerAsync(string connectionId, TcpClient local)
    {
        var buffer = new byte[4096];
        try
        {
            while (!_cancellationToken.IsCancellationRequested && local.Connected)
            {
                var read = await local.GetStream().ReadAsync(buffer, 0, buffer.Length, _cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                var data = Convert.ToBase64String(buffer, 0, read);
                var socket = _socket;
                if (socket is null || socket.State != WebSocketState.Open)
                {
                    break;
                }

                await SendMessageAsync(socket, new ClientMessage
                {
                    Type = MessageType.TcpData,
                    ConnectionId = connectionId,
                    Data = data
                });
            }
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  Erro lendo destino local: {ex.Message}");
        }
        finally
        {
            await CloseLocalAsync(connectionId);
        }
    }

    private async Task SendMessageAsync(ClientWebSocket socket, ClientMessage message)
    {
        var json = MessageSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, _cancellationToken);
    }

    private static async Task<string?> ReceiveStringAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        while (true)
        {
            try
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
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (WebSocketException)
            {
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async Task CloseLocalAsync(string connectionId)
    {
        TcpClient? client;
        lock (_connectionsLock)
        {
            if (!_localConnections.TryGetValue(connectionId, out client))
            {
                return;
            }
            _localConnections.Remove(connectionId);
        }

        try
        {
            client.Close();
        }
        catch
        {
            // ignore
        }
    }

    private async Task CleanupAsync()
    {
        var socket = _socket;
        try
        {
            if (socket is { State: WebSocketState.Open or WebSocketState.CloseReceived })
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None);
            }
        }
        catch
        {
            // ignore
        }

        lock (_connectionsLock)
        {
            foreach (var connection in _localConnections.Values)
            {
                try { connection.Close(); } catch { /* ignore */ }
            }
            _localConnections.Clear();
        }
    }
}
