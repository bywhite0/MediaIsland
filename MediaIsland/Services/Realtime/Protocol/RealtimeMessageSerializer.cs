using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaIsland.Services.Realtime.Protocol;

public static class RealtimeMessageSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static string Serialize(RealtimeMessage message) =>
        JsonSerializer.Serialize(message, Options);

    public static string SerializePayload<T>(T payload) =>
        JsonSerializer.Serialize(payload, Options);

    public static RealtimeMessage? Deserialize(string json) =>
        JsonSerializer.Deserialize<RealtimeMessage>(json, Options);

    public static RealtimeMessage? Deserialize(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<RealtimeMessage>(utf8Json, Options);

    public static T? DeserializePayload<T>(JsonElement? payload)
    {
        if (payload is null || payload.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        return payload.Value.Deserialize<T>(Options);
    }

    public static JsonElement? ToPayloadElement<T>(T? value)
    {
        if (value is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, Options));
        return document.RootElement.Clone();
    }

    public static RealtimeMessage Create(
        string type,
        object? payload = null,
        string? id = null,
        string? name = null,
        long? ts = null) =>
        new()
        {
            V = RealtimeProtocol.Version,
            Type = type,
            Id = id,
            Name = name,
            Ts = ts ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = payload is null ? null : ToPayloadElement(payload)
        };
}
