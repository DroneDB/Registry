using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Registry.Ports.Json;

/// <summary>
/// <c>double</c> written the way Json.NET did: the shortest round-trip text, with <c>.0</c> added to a whole number
/// (<c>1.0</c>, not <c>1</c>), so a number keeps its bytes between the Json.NET and System.Text.Json hosts; not finite
/// values are strings (<c>"NaN"</c>). Read from a number or from a string.
/// </summary>
public sealed class LegacyDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetDouble(),
            JsonTokenType.String => ParseString(reader.GetString()),
            _ => throw new JsonException($"A number is expected, not {reader.TokenType}")
        };

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (double.IsFinite(value))
            writer.WriteRawValue(WithDecimalPlace(value.ToString("R", CultureInfo.InvariantCulture)), skipInputValidation: true);
        else
            writer.WriteStringValue(NonFinite(value));
    }

    internal static double ParseString(string? text) =>
        text switch
        {
            "NaN" => double.NaN,
            "Infinity" => double.PositiveInfinity,
            "-Infinity" => double.NegativeInfinity,
            _ when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
            _ => throw new JsonException($"'{text}' is not a number")
        };

    internal static string NonFinite(double value) =>
        double.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity";

    internal static string WithDecimalPlace(string text) =>
        text.Contains('.') || text.Contains('E') || text.Contains('e') ? text : text + ".0";
}

/// <summary><c>float</c> as <see cref="LegacyDoubleConverter"/>.</summary>
public sealed class LegacySingleConverter : JsonConverter<float>
{
    public override float Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetSingle(),
            JsonTokenType.String => (float)LegacyDoubleConverter.ParseString(reader.GetString()),
            _ => throw new JsonException($"A number is expected, not {reader.TokenType}")
        };

    public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options)
    {
        if (float.IsFinite(value))
            writer.WriteRawValue(LegacyDoubleConverter.WithDecimalPlace(value.ToString("R", CultureInfo.InvariantCulture)),
                skipInputValidation: true);
        else
            writer.WriteStringValue(LegacyDoubleConverter.NonFinite(value));
    }
}
