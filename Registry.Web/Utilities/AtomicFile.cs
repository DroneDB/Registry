using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Registry.Ports;

namespace Registry.Web.Utilities;

/// <summary>
/// Writes a file so that a reader sees the old content or the new one, never a part (the storage
/// folder's <c>appsettings.json</c>, read by every Registry container sharing the folder). Every disk operation goes
/// through <see cref="IFileSystem"/>.
/// </summary>
public sealed class AtomicFile
{
    /// <summary>errno of a rename onto a mount point (Linux and macOS): a bind-mounted single file.</summary>
    private const int Ebusy = 16;

    /// <summary>Win32 errors of a file that another process holds open: sharing and lock violation.</summary>
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    /// <summary>How long a rename waits for the readers of the file, and a read for the rename (Windows).</summary>
    private static readonly TimeSpan HeldOpenTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Added to the name of a file that <see cref="CreateOrRead"/> creates: the folder that holds the content of the node that was first.</summary>
    private const string ClaimSuffix = ".claim";

    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly IFileSystem _fs;

    public AtomicFile(IFileSystem fs) => _fs = fs;

    /// <summary>
    /// Writes <paramref name="content"/> (UTF-8, no BOM) to a temporary file next to <paramref name="path"/>,
    /// flushes it to disk and renames it over <paramref name="path"/>. A file that is itself a mount point
    /// (Docker's <c>-v ./appsettings.json:/data/appsettings.json</c>) cannot be replaced by a rename
    /// (<c>EBUSY</c>): only then the prepared content is copied into it in place.
    /// </summary>
    /// <returns>True when the file was replaced atomically, false when it was written in place.</returns>
    public bool WriteAllText(string path, string content) => WriteAllText(path, content, Replace);

    /// <summary><see cref="WriteAllText(string,string)"/> with the rename step injected (tests).</summary>
    internal bool WriteAllText(string path, string content, Action<string, string> replace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(replace);

        var fullPath = Path.GetFullPath(path);
        var bytes = Utf8.GetBytes(content);
        var temp = WriteTemporary(fullPath, bytes);

        try
        {
            try
            {
                replace(temp, fullPath);
                return true;
            }
            catch (IOException ex) when (ex.HResult == Ebusy && !OperatingSystem.IsWindows())
            {
                WriteInPlace(fullPath, bytes);
                return false;
            }
        }
        finally
        {
            // No-op when the rename consumed it
            _fs.Delete(temp);
        }
    }

    /// <summary>
    /// The content of <paramref name="path"/>; when the file does not exist yet, <paramref name="content"/> (UTF-8, no
    /// BOM) is created as its content. Of several nodes that create it together exactly one's content becomes the file's:
    /// all of them return that one, and a reader never sees a part of it.
    /// </summary>
    /// <remarks>
    /// A move cannot decide who is first (it replaces an existing file), and a file created and then filled shows its
    /// reader an empty file. A directory cannot be moved onto a non-empty directory, so the content is prepared in a
    /// staging folder and the move of that folder to <c>{path}.claim</c> picks the winner; the claim is whole from the
    /// moment it exists. Every node then publishes the winner's content with the atomic write (the same bytes, so the order
    /// does not matter) and removes the claim, after publishing: whoever finds no claim finds the file.
    /// </remarks>
    /// <returns>The content of the file: the content this call gave, or the one a node that was first gave.</returns>
    public string CreateOrRead(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var fullPath = Path.GetFullPath(path);
        if (ReadIfExists(fullPath) is { } existing)
            return existing;

        var claim = fullPath + ClaimSuffix;
        var refused = Claim(fullPath, claim, content);

        // The claim of the winner (ours, or another node's), else the file the winner published and removed it after
        var winner = ReadIfExists(Path.Combine(claim, Path.GetFileName(fullPath))) ?? ReadIfExists(fullPath)
            ?? throw new IOException($"The claim on '{fullPath}' could not be made or read", refused);

        // A node that won the claim after the first one finished must not replace the file that one published
        if (ReadIfExists(fullPath) is { } published)
            winner = published;
        else
            WriteAllText(fullPath, winner);

        try
        {
            _fs.FolderDelete(claim, recursive: true);
        }
        catch (IOException)
        {
            // Another node is reading it (Windows): the claim stays, holding the content the file now has
        }
        catch (UnauthorizedAccessException)
        {
            // Same: a file that is open on Windows refuses to be deleted
        }

        return winner;
    }

    /// <summary>
    /// Moves a staging folder holding <paramref name="content"/> to <paramref name="claim"/>, unless a claim exists.
    /// </summary>
    /// <returns>Why the move was refused (a claim exists, as a rule); null when this call made the claim.</returns>
    private IOException? Claim(string fullPath, string claim, string content)
    {
        var staging = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        _fs.FolderCreate(staging);

        try
        {
            var staged = Path.Combine(staging, Path.GetFileName(fullPath));
            _fs.WriteAllBytesDurable(staged, Utf8.GetBytes(content), FileMode.CreateNew);

            try
            {
                _fs.FolderMove(staging, claim);
                return null;
            }
            catch (IOException ex)
            {
                // Another node holds the claim (it may have published and removed it by now): its content is the one that counts
                return ex;
            }
        }
        finally
        {
            if (_fs.FolderExists(staging))
                _fs.FolderDelete(staging, recursive: true);
        }
    }

    /// <summary>
    /// The content of <paramref name="path"/>. On Windows a file cannot be opened while a write of this class renames
    /// over it, or while it is being deleted: the read is tried again until <see cref="HeldOpenTimeout"/>.
    /// </summary>
    public string ReadAllText(string path) => WhileHeldOpen(() => _fs.ReadAllText(path));

    private string? ReadIfExists(string path)
    {
        try
        {
            return ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Writes <paramref name="bytes"/> to a new temporary file next to <paramref name="fullPath"/> and flushes it to disk.</summary>
    private string WriteTemporary(string fullPath, byte[] bytes)
    {
        var folder = Path.GetDirectoryName(fullPath) ?? ".";
        var temp = Path.Combine(folder, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            _fs.WriteAllBytesDurable(temp, bytes, FileMode.CreateNew);
            return temp;
        }
        catch
        {
            _fs.Delete(temp);
            throw;
        }
    }

    /// <summary>
    /// Renames <paramref name="source"/> over <paramref name="destination"/>. Windows refuses to replace a file that a
    /// reader holds open without <c>FILE_SHARE_DELETE</c> (as <c>File.ReadAllText</c> does): readers hold it briefly,
    /// so the rename is tried again until <see cref="HeldOpenTimeout"/>.
    /// </summary>
    private void Replace(string source, string destination) => WhileHeldOpen(() =>
    {
        _fs.Move(source, destination, overwrite: true);
        return true;
    });

    /// <summary><paramref name="operation"/>, tried again while Windows reports the file held open by another process.</summary>
    private static T WhileHeldOpen<T>(Func<T> operation)
    {
        var waited = Stopwatch.StartNew();
        var spin = new SpinWait();
        while (true)
        {
            try
            {
                return operation();
            }
            // A rename over a file held open, and an open of a file being deleted, fail with ERROR_ACCESS_DENIED (a real
            // permission problem looks the same and is reported after the timeout)
            catch (UnauthorizedAccessException) when (MayRetry(waited))
            {
                Wait(ref spin);
            }
            // An open during a rename fails with a sharing violation
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation &&
                                         MayRetry(waited))
            {
                Wait(ref spin);
            }
        }
    }

    // Yields, never Sleep(1): that wakes on the system timer tick (15.6 ms), as a reader that sleeps between reads does,
    // so each try would come at the moment the reader opens the file again
    private static void Wait(ref SpinWait spin) => spin.SpinOnce(sleep1Threshold: -1);

    private static bool MayRetry(Stopwatch waited) => OperatingSystem.IsWindows() && waited.Elapsed < HeldOpenTimeout;

    private void WriteInPlace(string path, byte[] bytes) => _fs.WriteAllBytesDurable(path, bytes, FileMode.Create, FileShare.Read);
}
