using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Utilities;

/// <summary>A download written to the response while it is produced (<see cref="StreamedDownloadResult"/>).</summary>
public interface IStreamedDownload
{
    /// <summary>The file name the download is offered under.</summary>
    string Name { get; }

    /// <summary>The media type sent with it.</summary>
    string ContentType { get; }

    /// <summary>Writes the download to <paramref name="stream"/>; stops when <paramref name="ct"/> is cancelled.</summary>
    Task CopyToAsync(Stream stream, CancellationToken ct = default);
}
