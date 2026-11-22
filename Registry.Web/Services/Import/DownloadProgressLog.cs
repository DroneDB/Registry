using Registry.Common;

namespace Registry.Web.Services.Import;

/// <summary>
/// The log lines of a download in progress: one per whole percent of a known size, one per
/// <see cref="UnknownSizeStepBytes"/> of an unknown one, not one per chunk read (a 100 MB archive wrote thousands). The
/// progress of the task still follows every chunk; only its log is stepped. One instance per download.
/// </summary>
public sealed class DownloadProgressLog
{
    /// <summary>How much a download of unknown size reads between two log lines.</summary>
    public const long UnknownSizeStepBytes = 10L * 1024 * 1024;

    private long _lastStep = -1;

    /// <summary>The line to log for <paramref name="progress"/>, or null when it is in the step of the last line.</summary>
    public string? LineFor(FileDownloadProgress progress)
    {
        var step = progress.TotalBytes is > 0 and var total
            ? progress.BytesSoFar * 100 / total
            : progress.BytesSoFar / UnknownSizeStepBytes;
        if (step == _lastStep)
            return null;

        _lastStep = step;
        return progress.TotalBytes is > 0
            ? $"Downloaded {CommonUtils.GetBytesReadable(progress.BytesSoFar)} / {CommonUtils.GetBytesReadable(progress.TotalBytes.Value)}"
            : $"Downloaded {CommonUtils.GetBytesReadable(progress.BytesSoFar)}";
    }
}
