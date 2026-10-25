using System.Buffers;
using System.Collections;
using System.Text.Encodings.Web;
using System.Text.Json;
using Registry.Common;
using Registry.Ports.Json;

namespace Registry.Web.Identity;

/// <summary>
/// How <c>User.Metadata</c> is stored in its column: JSON text, written and read with System.Text.Json (the
/// column was Json.NET text until then, and the text is the same, so no stored row changes and an older Registry reads
/// what this one writes).
/// <para>
/// The values are plain CLR objects on both sides (<see cref="PlainValue"/>): <c>string</c>, <c>long</c>, <c>double</c>,
/// <c>bool</c>, <c>null</c>, <c>List&lt;object?&gt;</c> and <c>Dictionary&lt;string, object?&gt;</c>. A value read from JSON by
/// the API (<see cref="JsonElement"/>) is written as the JSON it holds; any other value (an <c>int</c>, a
/// <c>DateTime</c>, an object a provider returned) is written as the serializer writes it. What the text looks like is
/// what Json.NET wrote: compact, a whole <c>double</c> with <c>.0</c> (<see cref="LegacyDoubleConverter"/>), no escaping of
/// markup or non-ASCII, keys in the order of the dictionary. A string is never read as a date.
/// </para>
/// </summary>
public static class UserMetadataJson
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions ValueOptions = CreateValueOptions();

    private static readonly LegacyDoubleConverter Doubles = new();

    /// <summary>The stored text of <paramref name="metadata"/>.</summary>
    public static string Serialize(Dictionary<string, object> metadata)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            WriteValue(writer, metadata);

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The metadata of a stored text (null for the JSON literal <c>null</c>).</summary>
    /// <exception cref="JsonException">The text is not JSON, or not a JSON object.</exception>
    public static Dictionary<string, object>? Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Object => (Dictionary<string, object>)(object)PlainValue.From(document.RootElement)!,
            var kind => throw new JsonException($"User metadata is a JSON object, not {kind}")
        };
    }

    /// <summary>A deep copy (EF's snapshot of the value).</summary>
    public static Dictionary<string, object> Clone(Dictionary<string, object> metadata) => PlainValue.Clone(metadata);

    /// <summary>EF's equality of two metadata: by content (<see cref="PlainValue.AreEqual"/>).</summary>
    public static bool AreEqual(Dictionary<string, object> x, Dictionary<string, object> y) => PlainValue.AreEqual(x, y);

    /// <summary>EF's hash of a metadata: by content, the same for values <see cref="AreEqual"/> calls equal.</summary>
    public static int GetHashCode(Dictionary<string, object> metadata) => PlainValue.GetHashCode(metadata);

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                Doubles.Write(writer, number, ValueOptions);
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;
            case IDictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var (key, item) in map)
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, item);
                }
                writer.WriteEndObject();
                break;
            case IEnumerable list and not IDictionary:
                writer.WriteStartArray();
                foreach (var item in list)
                    WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            default:
                JsonSerializer.Serialize(writer, value, value.GetType(), ValueOptions);
                break;
        }
    }

    private static JsonSerializerOptions CreateValueOptions()
    {
        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        options.Converters.Add(new LegacyDoubleConverter());
        options.Converters.Add(new LegacySingleConverter());
        return options;
    }
}
