#nullable disable
namespace Registry.Web.Models.DTO;

public class ChunkUploadDto
{
    /// <summary>
    /// The unique identifier for the file being uploaded
    /// </summary>
    public string FileId { get; set; }
    
    /// <summary>
    /// The index of the current chunk (0-based)
    /// </summary>
    public int ChunkIndex { get; set; }
    
    /// <summary>
    /// Total number of chunks for this file
    /// </summary>
    public int TotalChunks { get; set; }
    
    /// <summary>
    /// The total size of the file in bytes
    /// </summary>
    public long TotalFileSize { get; set; }
    
    /// <summary>
    /// The original filename
    /// </summary>
    public string FileName { get; set; }
    
    /// <summary>
    /// Path where the file should be stored
    /// </summary>
    public string Path { get; set; }
    
    /// <summary>
    /// Size of every chunk but the last (required when the file has more than one chunk): chunks
    /// are written at their offset in the file as they arrive
    /// </summary>
    public long? ChunkSize { get; set; }
}

public class ChunkUploadResultDto
{
    /// <summary>
    /// The unique identifier for the file being uploaded
    /// </summary>
    public string FileId { get; set; }
    
    /// <summary>
    /// The index of the received chunk
    /// </summary>
    public int ReceivedChunk { get; set; }
    
    /// <summary>
    /// Success status of the chunk upload
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// Set to true when all chunks have been received and the file is complete
    /// </summary>
    public bool IsComplete { get; set; }
    
    /// <summary>
    /// Hash of the complete file (only returned when IsComplete is true)
    /// </summary>
    public string Hash { get; set; }
    
    /// <summary>
    /// Size of the complete file (only returned when IsComplete is true)
    /// </summary>
    public long Size { get; set; }
}
