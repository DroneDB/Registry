#nullable enable
namespace Registry.Web.Models.Configuration;

/// <summary>
/// Where <see cref="Services.Ports.IScratchFileProvider"/> writes (RQ-10). Bound from
/// <c>AppSettings:TempPath</c> in <c>AddDatasetFileSystem</c>: that one binding is what the options
/// split (RQ-07) changes.
/// </summary>
public sealed class ScratchOptions
{
    /// <summary>The temp folder; a relative path resolves against the current folder (the storage folder).</summary>
    public string? TempPath { get; set; }
}
