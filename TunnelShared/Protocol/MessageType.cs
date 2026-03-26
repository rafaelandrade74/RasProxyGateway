namespace TunnelShared.Protocol;

public enum MessageType
{
    Unknown,
    Auth,
    Authenticated,
    CreateTunnel,
    TunnelCreated,
    ConnectTunnel,
    TunnelConnected,
    TcpData,
    Error
}

public static class MessageTypeHelpers
{
    public static MessageType Parse(string? value) =>
        Enum.TryParse<MessageType>(value, ignoreCase: true, out var parsed)
            ? parsed
            : MessageType.Unknown;
}
