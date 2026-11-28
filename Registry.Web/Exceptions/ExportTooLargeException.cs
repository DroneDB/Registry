using Registry.Web.Utilities;

namespace Registry.Web.Exceptions;

/// <summary>
/// An export of a raster whose estimated size is over the export limit (HTTP 400, D-023, FR-RAS-16): the estimate and the
/// limit, which <see cref="ApiErrorResponder"/> writes as the <see cref="ApiErrorResponder.EstimatedExportBytesMember"/>
/// and <see cref="ApiErrorResponder.ExportLimitBytesMember"/> members of the ProblemDetails body, the numbers the value
/// info of the same raster (and window) shows.
/// </summary>
public sealed class ExportTooLargeException : BadRequestException
{
    /// <summary>Creates the refusal of an export of <paramref name="estimatedBytes"/> bytes over <paramref name="limitBytes"/>.</summary>
    public ExportTooLargeException(string message, long estimatedBytes, long limitBytes)
        : base(message, noRetry: true)
    {
        EstimatedBytes = estimatedBytes;
        LimitBytes = limitBytes;
    }

    /// <summary>The estimated size of the export, in bytes.</summary>
    public long EstimatedBytes { get; }

    /// <summary>The export limit, in bytes.</summary>
    public long LimitBytes { get; }
}
