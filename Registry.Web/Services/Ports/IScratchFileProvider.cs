using System;
using Registry.Ports;

namespace Registry.Web.Services.Ports;

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
