using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Registry.Ports.Json;

/// <summary>
/// <c>string</c> read as Json.NET read it: a JSON string, or a number or boolean taken as its text (<c>12345</c> is
/// <c>"12345"</c>, <c>true</c> is <c>"true"</c>): a script that sends a number where the API wants text keeps working.
/// An object or an array is refused. Written as a JSON string.
/// </summary>
public sealed class LenientStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            JsonTokenType.Number => Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => throw new JsonException($"A string is expected, not {reader.TokenType}")
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);

    // A dictionary key is always a string
    public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() ?? throw new JsonException("A property name is expected");

    public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value);
}
