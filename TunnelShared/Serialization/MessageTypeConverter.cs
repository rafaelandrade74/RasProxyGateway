using System.Text.Json;
using System.Text.Json.Serialization;
using TunnelShared.Protocol;

namespace TunnelShared.Serialization;

public sealed class MessageTypeConverter : JsonConverter<MessageType>
{
    public override MessageType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            return MessageType.Unknown;
        }

        var value = reader.GetString();
        return MessageTypeHelpers.Parse(value);
    }

    public override void Write(Utf8JsonWriter writer, MessageType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
