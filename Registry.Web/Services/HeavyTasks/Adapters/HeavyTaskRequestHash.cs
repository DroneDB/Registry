using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Registry.Ports.DroneDB;
using Registry.Web.Services.HeavyTasks.Models;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// Stable hash of a heavy-task request (tool, version, source path, content hash of the source, params with sorted keys),
/// stored as <c>JobIndex.RequestHash</c> and used to deduplicate submits. Two submits that differ only by their path
/// are two requests: the contours or the export of one raster are not those of another raster with the same options.
/// The content hash makes a file replaced at the same path a new request too, so its task does not reuse the result
/// of the file it replaced.
/// </summary>
public static class HeavyTaskRequestHash
{
    /// <summary>
    /// The hash of <paramref name="request"/>: <paramref name="contentHash"/> when the caller knows the content it
    /// submits for (a build task), else the hash of the entry at the path of the request (none for a folder, a path
    /// that is not indexed or a request without path).
    /// </summary>
    public static string Of(HeavyToolRequest request, IDDB ddb, string? contentHash = null) =>
        Compute(request.ToolId, request.Version, request.Path,
            contentHash ?? (string.IsNullOrEmpty(request.Path) ? null : ddb.GetEntry(request.Path)?.Hash),
            request.Params);

    public static string Compute(string toolId, string version, string? path, string? contentHash, JsonElement prms)
    {
        // The path is a JSON string, so no path can read as another path followed by a content hash
        var canonical = $"{toolId}|{version}|{JsonSerializer.Serialize(path)}|{contentHash}|{CanonicalJson(prms)}";
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
