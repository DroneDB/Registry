using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Registry.Ports;

/// <summary>
/// JSON converter for Unix epoch timestamp (seconds) to DateTime: libddb writes times as epoch seconds, and so does the
/// Registry API (<c>mtime</c>, <c>captureTime</c>: <c>1700000000</c>, or <c>1700000000.5</c> with a fraction).
/// </summary>
public class SecondEpochConverter : JsonConverter<DateTime>
{
    private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteRawValue(((value - Epoch).TotalMilliseconds / 1000.0).ToString(CultureInfo.InvariantCulture),
            skipInputValidation: true);

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number)
            throw new JsonException($"Epoch seconds are a number, not {reader.TokenType}");

        return Epoch.AddSeconds(reader.TryGetInt64(out var whole) ? whole : reader.GetDouble());
    }
}
