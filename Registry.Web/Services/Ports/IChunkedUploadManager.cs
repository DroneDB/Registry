namespace Registry.Web.Services.Ports;

/// <summary>A chunk of a resumable upload, as sent by the client.</summary>
/// <param name="FileId">Client-generated id of the upload (8 to 64 letters, digits, '-' or '_').</param>
/// <param name="ChunkIndex">0-based index of the chunk.</param>
/// <param name="TotalChunks">Number of chunks.</param>
/// <param name="TotalFileSize">Size of the whole file.</param>
/// <param name="ChunkSize">Size of every chunk but the last.</param>
/// <param name="Path">Destination path in the dataset.</param>
/// <param name="Overwrite">Whether the upload may replace a file that holds its name (read from the first chunk).</param>
/// <param name="SourceFileName">The name of the file on the client's disk (the <c>fileName</c> field, first chunk).</param>
/// <param name="LastModified">Its last-modified time, milliseconds since 1970 (the <c>lastModified</c> field, first chunk).</param>
public sealed record ChunkRequest(string FileId, int ChunkIndex, int TotalChunks, long TotalFileSize, long ChunkSize,
    string Path, bool Overwrite = true, string? SourceFileName = null, long? LastModified = null);
