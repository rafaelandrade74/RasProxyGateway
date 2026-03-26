using System.Text.Json;
using TunnelShared.Protocol;

namespace TunnelShared.Serialization;

public static class MessageSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new MessageTypeConverter() }
    };

    public static string Serialize(ClientMessage message) =>
        JsonSerializer.Serialize(message, Options);

    public static ClientMessage? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ClientMessage>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
