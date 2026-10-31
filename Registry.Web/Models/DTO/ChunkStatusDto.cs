namespace Registry.Web.Models.DTO;

/// <summary>The state of a resumable chunked upload.</summary>
public class ChunkStatusDto
{
    public string FileId { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long TotalFileSize { get; set; }
    public int TotalChunks { get; set; }
    public long ChunkSize { get; set; }

    /// <summary>Chunks stored, in order.</summary>
    public int[] Received { get; set; } = [];

    /// <summary>Chunks still to send, in order.</summary>
    public int[] Missing { get; set; } = [];

    /// <summary>Every chunk is stored: the upload can be committed.</summary>
    public bool Complete { get; set; }
}
