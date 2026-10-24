using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Registry.Ports.Json;

/// <summary>
/// <c>DateTime</c> as an ISO 8601 UTC instant (<c>2026-10-21T07:31:17.123Z</c>, the fraction trimmed of trailing zeros):
/// what the MVC Json.NET settings did with <c>DateTimeZoneHandling.Utc</c>. A value of unspecified kind (what EF returns
/// for a stored date) is the UTC instant it was stored as, not shifted; a local one is converted. Read the same way:
/// the result is always UTC.
/// </summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"A date-time is a string, not {reader.TokenType}");

        return ToUtc(reader.GetDateTime());
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToUtc(value));

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime()
    };
}
