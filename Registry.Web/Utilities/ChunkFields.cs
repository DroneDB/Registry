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
            Long(fields, "totalFileSize"), Long(fields, "chunkSize"), Required(fields, "path"), Overwrite(fields),
            Optional(fields, "fileName"), OptionalLong(fields, "lastModified"));

    /// <summary>
    /// The <c>overwrite</c> field of an upload (D-040): true only when it is sent as <c>true</c> (any case) or
    /// <c>1</c>; without it an upload does not replace a file that holds its name.
    /// </summary>
    /// <exception cref="BadRequestException">The field is neither a boolean nor 0 or 1.</exception>
    public static bool Overwrite(IReadOnlyDictionary<string, string> fields) =>
        Overwrite(fields.TryGetValue("overwrite", out var value) ? value : null);

    /// <summary>The value of an <c>overwrite</c> field (see <see cref="Overwrite(IReadOnlyDictionary{string, string})"/>); null: false.</summary>
    /// <exception cref="BadRequestException">Neither a boolean nor 0 or 1.</exception>
    public static bool Overwrite(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        value = value.Trim();
        if (bool.TryParse(value, out var flag))
            return flag;
        return value switch
        {
            "1" => true,
            "0" => false,
            _ => throw new BadRequestException("'overwrite' must be true or false")
        };
    }

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

    private static string? Optional(IReadOnlyDictionary<string, string> fields, string name) =>
        fields.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static long? OptionalLong(IReadOnlyDictionary<string, string> fields, string name) =>
        Optional(fields, name) == null ? null : Long(fields, name);

    private static int Int(IReadOnlyDictionary<string, string> fields, string name) =>
        int.TryParse(Required(fields, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new BadRequestException($"'{name}' is not a number");

    private static long Long(IReadOnlyDictionary<string, string> fields, string name) =>
        long.TryParse(Required(fields, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new BadRequestException($"'{name}' is not a number");
}
