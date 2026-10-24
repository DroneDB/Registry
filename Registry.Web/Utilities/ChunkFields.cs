#nullable enable
using System.Collections.Generic;
using System.Globalization;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Utilities;

/// <summary>
/// Reads the form fields of a chunk request (sent before the chunk part, case-insensitive), for the
/// dataset and share chunked uploads alike.
/// </summary>
public static class ChunkFields
{
    public static ChunkRequest ToDatasetChunk(IReadOnlyDictionary<string, string> fields) =>
        new(Required(fields, "fileId"), Int(fields, "chunkIndex"), Int(fields, "totalChunks"),
            Long(fields, "totalFileSize"), Long(fields, "chunkSize"), Required(fields, "path"));

    public static ChunkUploadDto ToShareChunk(IReadOnlyDictionary<string, string> fields) => new()
    {
        FileId = Required(fields, "fileId"),
        ChunkIndex = Int(fields, "chunkIndex"),
        TotalChunks = Int(fields, "totalChunks"),
        TotalFileSize = Long(fields, "totalFileSize"),
        ChunkSize = fields.TryGetValue("chunkSize", out var size) && !string.IsNullOrWhiteSpace(size) ? Long(fields, "chunkSize") : null,
        Path = Required(fields, "path"),
        FileName = fields.TryGetValue("fileName", out var name) ? name : null
    };

    private static string Required(IReadOnlyDictionary<string, string> fields, string name) =>
        fields.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new BadRequestException($"Missing '{name}' (form fields must come before the chunk)");

    private static int Int(IReadOnlyDictionary<string, string> fields, string name) =>
        int.TryParse(Required(fields, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new BadRequestException($"'{name}' is not a number");

    private static long Long(IReadOnlyDictionary<string, string> fields, string name) =>
        long.TryParse(Required(fields, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new BadRequestException($"'{name}' is not a number");
}
