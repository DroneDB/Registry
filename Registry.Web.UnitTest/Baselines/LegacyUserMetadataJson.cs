using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The oracle of the user metadata tests: <c>UserMetadataJson</c> as it was before the column moved to System.Text.Json (Json.NET text), copied
/// verbatim apart from its name. Every row an older Registry stored was written by this code, and an older Registry
/// reads with it what the new one writes (a downgrade): the tests run both directions over the same values.
/// <para>
/// The values are plain CLR objects on both sides: <c>string</c>, <c>long</c>, <c>double</c>, <c>bool</c>, <c>null</c>,
/// <c>List&lt;object?&gt;</c> and <c>Dictionary&lt;string, object?&gt;</c>. A value read from JSON by the API
/// (<see cref="System.Text.Json.JsonElement"/>) is written as the JSON it holds; a value read from the column is never a Json.NET token,
/// so no Json.NET type leaves this assembly.
/// </para>
/// </summary>
public static class LegacyUserMetadataJson
{
    private static readonly JsonSerializerSettings WriteSettings = new() { Converters = { new JsonElementWriter() } };

    // Strings stay strings: a date-looking value of a login provider is not turned into a DateTime on its way back
    private static readonly JsonSerializerSettings ReadSettings = new() { DateParseHandling = DateParseHandling.None };

    /// <summary>The stored text of <paramref name="metadata"/>.</summary>
    public static string Serialize(Dictionary<string, object> metadata) =>
        JsonConvert.SerializeObject(metadata, WriteSettings);

    /// <summary>The metadata of a stored text (null for the JSON literal <c>null</c>).</summary>
    public static Dictionary<string, object>? Deserialize(string json) =>
        JsonConvert.DeserializeObject<JObject>(json, ReadSettings) is { } root
            ? (Dictionary<string, object>)Plain(root)!
            : null;

    /// <summary>A deep copy (EF's snapshot of the value).</summary>
    public static Dictionary<string, object> Clone(Dictionary<string, object> metadata) =>
        Deserialize(Serialize(metadata)) ?? new();

    private static object? Plain(JToken token) => token switch
    {
        JObject o => o.Properties().ToDictionary(p => p.Name, p => Plain(p.Value), StringComparer.Ordinal),
        JArray a => a.Select(Plain).ToList(),
        JValue v => v.Value,
        _ => token.ToString()
    };

    private sealed class JsonElementWriter : JsonConverter<System.Text.Json.JsonElement>
    {
        public override void WriteJson(JsonWriter writer, System.Text.Json.JsonElement value, JsonSerializer serializer) =>
            writer.WriteRawValue(value.GetRawText());

        public override System.Text.Json.JsonElement ReadJson(JsonReader reader, Type objectType, System.Text.Json.JsonElement existingValue,
            bool hasExistingValue, JsonSerializer serializer) =>
            throw new NotSupportedException("Metadata is read as plain values");
    }
}
