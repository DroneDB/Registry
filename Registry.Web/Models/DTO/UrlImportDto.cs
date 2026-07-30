#nullable enable
using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Request to verify (probe) a single-file URL before importing it into a dataset.
/// </summary>
public sealed class UrlImportVerifyRequestDto
{
    /// <summary>The absolute http/https URL of the file to import.</summary>
    [Required]
    public string Url { get; set; } = string.Empty;

    /// <summary>Optional HTTP basic-auth user name.</summary>
    public string? Username { get; set; }

    /// <summary>Optional HTTP basic-auth password.</summary>
    public string? Password { get; set; }
}

/// <summary>
/// Result of a single-file URL verification: reachability plus best-effort size, the derived file
/// name and the outcome of the deny-list / size checks.
/// </summary>
public sealed class UrlImportVerifyResultDto
{
    /// <summary>True when the URL responded successfully.</summary>
    public bool Reachable { get; set; }

    /// <summary>The advertised size in bytes, when known.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>The file name derived from the server response or the URL.</summary>
    public string? FileName { get; set; }

    /// <summary>True when the derived file type is on the import deny-list.</summary>
    public bool Blocked { get; set; }

    /// <summary>True when the advertised size exceeds the per-file import cap.</summary>
    public bool SizeExceedsLimit { get; set; }

    /// <summary>Human-readable status or warning message.</summary>
    public string? Note { get; set; }
}

/// <summary>
/// Request to import a single file from a URL into an existing dataset (submits the
/// <c>import-file</c> heavy task).
/// </summary>
public sealed class UrlImportRequestDto
{
    /// <summary>The absolute http/https URL of the file to import.</summary>
    [Required]
    public string Url { get; set; } = string.Empty;

    /// <summary>The destination file name (derived from the URL when omitted).</summary>
    public string? FileName { get; set; }

    /// <summary>Optional destination folder within the dataset (root when omitted).</summary>
    public string? Folder { get; set; }

    /// <summary>Whether to overwrite an existing file at the destination path.</summary>
    public bool Overwrite { get; set; }

    /// <summary>Optional HTTP basic-auth user name.</summary>
    public string? Username { get; set; }

    /// <summary>Optional HTTP basic-auth password (encrypted at rest before enqueue).</summary>
    public string? Password { get; set; }

    /// <summary>Optional advertised size (bytes) captured during verification.</summary>
    public long? SizeBytes { get; set; }
}

/// <summary>Result of submitting a single-file URL import.</summary>
public sealed class UrlImportResultDto
{
    /// <summary>The tracking task id of the submitted <c>import-file</c> task.</summary>
    public string TaskId { get; set; } = string.Empty;
}
