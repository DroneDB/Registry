using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Registry.Ports.Json;

/// <summary>
/// Reading a libddb document (<see cref="JsonElement"/>) without throwing for a member that is not there: what the
/// services that look inside a document (layers, bands, geometries) need, in one place.
/// </summary>
public static class JsonElementExtensions
{
    /// <summary>The member <paramref name="name"/> of an object, or null when the value is not an object or has none.</summary>
    public static JsonElement? Member(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    /// <summary>The member <paramref name="name"/> of an object (null: none).</summary>
    public static JsonElement? Member(this JsonElement? element, string name) => element?.Member(name);

    /// <summary>The text of a string value (null for null and for none); any other value as its JSON text.</summary>
    public static string? Text(this JsonElement? element) =>
        element is not { } value ? null : value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText()
        };

    /// <summary>The number of a number value, or of a string that holds one; throws for any other value.</summary>
    public static double ToDouble(this JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String => double.Parse(element.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"A {element.ValueKind} is not a number")
        };

    /// <summary>The elements of an array (none for another value).</summary>
    public static IEnumerable<JsonElement> Items(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];

    /// <summary>
    /// The value as plain CLR objects: <c>string</c>, <c>long</c> (a whole number that fits), <c>double</c>, <c>bool</c>,
    /// <c>null</c>, <c>List&lt;object?&gt;</c> and <c>Dictionary&lt;string, object?&gt;</c>. For the places that keep a
    /// free-form value in an <c>object</c> (a login provider's metadata) and must not hold a JSON library's type.
    /// </summary>
    public static object? ToPlain(this JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                    dictionary[property.Name] = property.Value.ToPlain();
                return dictionary;
            case JsonValueKind.Array:
                var list = new List<object?>(element.GetArrayLength());
                foreach (var item in element.EnumerateArray())
                    list.Add(item.ToPlain());
                return list;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return element.TryGetInt64(out var whole) ? whole : element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    /// <summary>An object as a dictionary of <see cref="ToPlain"/> values (null for another value).</summary>
    public static Dictionary<string, object>? ToPlainDictionary(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? (Dictionary<string, object>)(object)((Dictionary<string, object?>)element.ToPlain()!)
            : null;
}
