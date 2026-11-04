using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Registry.Ports.Json;

/// <summary>
/// A <c>DateTime</c> as whole Unix epoch seconds (<c>1761000000</c>), truncated: what Json.NET's
/// <c>UnixDateTimeConverter</c> wrote for the token expiry. A value of unspecified kind is local time, as there.
/// </summary>
public sealed class UnixSecondsConverter : JsonConverter<DateTime>
{
    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteNumberValue((long)(value.ToUniversalTime() - Epoch).TotalSeconds);

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number
            ? Epoch.AddSeconds(reader.GetInt64())
            : throw new JsonException($"Epoch seconds are a number, not {reader.TokenType}");
}
