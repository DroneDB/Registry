#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using Registry.Web.Utilities;

namespace Registry.Web.Test.Support;

/// <summary>JSON values for tests (RQ-14): the documents the ports carry are <see cref="JsonElement"/>s.</summary>
public static class TestJson
{
    /// <summary><paramref name="value"/> as the JSON document the API would write for it.</summary>
    public static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value, ApiJson.Options);

    /// <summary>A JSON document from its text.</summary>
    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>The properties of an entry from plain values (each written as JSON).</summary>
    public static Dictionary<string, JsonElement> Props(IEnumerable<KeyValuePair<string, object?>> values)
    {
        var properties = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in values)
            properties[key] = Element(value);
        return properties;
    }

    /// <summary>The properties of an entry from plain values (each written as JSON).</summary>
    public static Dictionary<string, JsonElement> Props(params (string Key, object? Value)[] values) =>
        Props(System.Linq.Enumerable.Select(values, v => new KeyValuePair<string, object?>(v.Key, v.Value)));
}
