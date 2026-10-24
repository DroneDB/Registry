using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Registry.Ports.Json;

/// <summary>
/// A JSON object as a <c>Dictionary&lt;string, JsonElement&gt;</c> whose values are views of one document: the object is
/// copied out of the reader once (the buffer it comes from is native memory that is freed afterwards), where the
/// default converter copies every value into a document of its own. For the properties of an entry (a dozen
/// values each, 50 000 entries in a listing) that is a document per entry instead of one per value.
/// </summary>
public sealed class SharedDocumentMapConverter : JsonConverter<Dictionary<string, JsonElement>>
{
    public override Dictionary<string, JsonElement>? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        // Not disposed: the elements outlive this call, like any JsonElement the serializer hands out
        var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException($"A JSON object is expected, not {root.ValueKind}");

        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            map[property.Name] = property.Value;
        return map;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, JsonElement> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, element) in value)
        {
            writer.WritePropertyName(key);
            element.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}
