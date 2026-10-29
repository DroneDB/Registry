namespace Registry.Web.Models.DTO;

/// <summary>The result of one file of an upload request.</summary>
public class UploadPartResultDto
{
    /// <summary>Destination path of the file in the dataset.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>HTTP status of this file: 201 when added, otherwise the status of its error.</summary>
    public int Status { get; set; }

    /// <summary>The added entry, when <see cref="Status"/> is 201.</summary>
    public EntryDto? Entry { get; set; }

    /// <summary>Why the file was not added.</summary>
    public string? Error { get; set; }

    /// <summary>Whether sending the file again can succeed (the error is transient).</summary>
    public bool Retryable { get; set; }
}
