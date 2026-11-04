using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.Ports;

namespace Registry.Web.Utilities;

/// <summary>
/// A ZIP archive of what is in a folder of the filesystem (a directory artifact of a build, such as 3D Tiles), written
/// to the response entry by entry: the folder's content is at the root of the archive.
/// </summary>
public sealed class FolderArchiveDownload : IStreamedDownload
{
    private readonly string _folder;
    private readonly IZipArchiveBuilder _zip;

    public FolderArchiveDownload(string name, string folder, IZipArchiveBuilder zip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Name = name;
        _folder = folder;
        _zip = zip ?? throw new ArgumentNullException(nameof(zip));
    }

    public string Name { get; }

    public string ContentType => "application/zip";

    public Task CopyToAsync(Stream stream, CancellationToken ct = default) => _zip.WriteFolderZipAsync(_folder, stream, ct);
}
