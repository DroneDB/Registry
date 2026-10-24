#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// Stable hash of a heavy-task request (tool, version, entry hash, params with sorted keys),
/// stored as <c>JobIndex.RequestHash</c> and used to deduplicate submits.
/// </summary>
public static class HeavyTaskRequestHash
{
    public static string Compute(string toolId, string version, string? entryHash, JsonElement prms)
    {
        var canonical = $"{toolId}|{version}|{entryHash}|{CanonicalJson(prms)}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string CanonicalJson(JsonElement element)
    {
        // Guard: Undefined means the field was absent from the JSON body.
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return "null";

        // Stable serialization for dedup: sort object keys recursively.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(element, writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var props = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var p in element.EnumerateObject())
                    props[p.Name] = p.Value;
                foreach (var kv in props)
                {
                    writer.WritePropertyName(kv.Key);
                    WriteCanonical(kv.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
