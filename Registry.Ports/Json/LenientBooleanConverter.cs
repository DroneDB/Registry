using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Registry.Ports.Json;

/// <summary>
/// <c>bool</c> read as Json.NET read it: <c>true</c>/<c>false</c>, the strings <c>"true"</c>/<c>"false"</c> in any case, and a
/// number (zero is false). Written as a JSON boolean.
/// </summary>
public sealed class LenientBooleanConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.String when bool.TryParse(reader.GetString(), out var value) => value,
            JsonTokenType.Number when reader.TryGetDouble(out var number) => number != 0,
            _ => throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"A boolean is expected, not {reader.TokenType}"))
        };

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        writer.WriteBooleanValue(value);
}
