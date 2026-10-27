#nullable enable
using System;
using System.IO;
using System.Text;
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

    private string? ReadIfExists(string path)
    {
        try
        {
            return _fs.ReadAllText(path);
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

    private void Replace(string source, string destination) => _fs.Move(source, destination, overwrite: true);

    private void WriteInPlace(string path, byte[] bytes) => _fs.WriteAllBytesDurable(path, bytes, FileMode.Create, FileShare.Read);
}
