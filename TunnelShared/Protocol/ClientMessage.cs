using TunnelShared.Serialization;

namespace TunnelShared.Protocol;

/// <summary>
/// Mensagens trocadas do cliente para o servidor (e em alguns casos de volta, como tcp_data).
/// </summary>
public sealed class ClientMessage
{
    public MessageType Type { get; set; }
    public string? Token { get; set; }
    public string? TunnelId { get; set; }
    public string? ConnectionId { get; set; }
    public string? Data { get; set; }
    public string? ClientId { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
    public string? RemoteIp { get; set; }
    public int? PublicPort { get; set; }
}
