#nullable enable
using System;
using Registry.Ports;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Where the Registry writes files that are not (yet) part of a dataset (RQ-10). Two places, by what
/// happens to the file next:
/// <list type="bullet">
/// <item><see cref="Create"/>: under the configured temp folder (<c>AppSettings.TempPath</c>), for a
/// file that is read and deleted (a downloaded archive, an export, a task folder).</item>
/// <item><see cref="CreateForDataset"/>: in the dataset's uploads folder, the same volume as the
/// dataset, for a file that is moved into it later: the move is a rename, never a copy.</item>
/// </list>
/// </summary>
public interface IScratchFileProvider
{
    /// <summary>The configured temp folder, absolute (created on first use).</summary>
    string Root { get; }

    /// <summary>A new file name under <see cref="Root"/>: <paramref name="prefix"/>, a GUID, <paramref name="extension"/>.</summary>
    ScratchFile Create(string prefix, string extension);

    /// <summary>
    /// A new file name in the uploads folder of the dataset at <paramref name="datasetFolder"/>
    /// (created on first use). The abandoned-upload sweep removes a <c>.tmp</c> file left there.
    /// </summary>
    ScratchFile CreateForDataset(string datasetFolder, string extension = ".tmp");
}

/// <summary>
/// A scratch file name, not yet written. Disposing deletes the file (best effort,
/// <see cref="FileSystemExtensions.TryDelete"/>) unless <see cref="Release"/> was called because it was
/// moved into place or handed over to an owner that deletes it.
/// </summary>
public sealed class ScratchFile : IDisposable
{
    private readonly IFileSystem _fs;
    private readonly Action<string, Exception> _onDeleteError;
    private bool _released;

    public ScratchFile(string path, IFileSystem fs, Action<string, Exception> onDeleteError)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _onDeleteError = onDeleteError ?? throw new ArgumentNullException(nameof(onDeleteError));
    }

    /// <summary>The absolute path of the file.</summary>
    public string Path { get; }

    /// <summary>The file was moved or handed over: <see cref="Dispose"/> leaves it alone.</summary>
    public void Release() => _released = true;

    public void Dispose()
    {
        if (_released)
            return;
        _released = true;
        _fs.TryDelete(Path, ex => _onDeleteError(Path, ex));
    }
}
