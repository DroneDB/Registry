using System;
using System.Collections.Generic;
using System.IO;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Services.Import;

namespace Registry.Web.Utilities;

/// <summary>
/// The one dataset path rule of the Registry: which paths name something in a dataset, and
/// how each is spelled. The rule is libddb's (<c>DDBNormalizePath</c>, through
/// <see cref="IDdbWrapper.NormalizePath"/>; golden vectors in DroneDB
/// <c>tests/contract/vectors/entry-paths.json</c>), so Registry and libddb accept and reject
/// the same paths. Registry adds two things: <see cref="IDDB.UploadsFolderName"/> is reserved as the
/// first segment (its staging and quarantine folder), and a whitespace-only path is empty.
/// <para>
/// Every entry point that receives a dataset path calls <see cref="Normalize"/> (or
/// <see cref="Check"/>) and works with the value it returns: '/' separated, no empty or "." segment,
/// no leading or trailing separator.
/// </para>
/// </summary>
public sealed class DatasetPathPolicy
{
    /// <summary>The names reserved as the first segment of a dataset path (".ddb" is libddb's, at any depth).</summary>
    public static IReadOnlyList<string> RootReservedNames { get; } = [IDDB.UploadsFolderName];

    private static readonly char[] Separators = ['/', '\\'];

    private readonly IDdbWrapper _ddb;

    public DatasetPathPolicy(IDdbWrapper ddb)
    {
        _ddb = ddb;
    }

    /// <summary>
    /// The normalized form of <paramref name="path"/> or the rule it breaks
    /// (<see cref="EntryPathRejection"/>). Null and whitespace-only paths are empty.
    /// </summary>
    public NormalizedEntryPath Check(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? NormalizedEntryPath.Rejection(EntryPathRejection.Empty)
            : _ddb.NormalizePath(path, RootReservedNames);

    /// <summary>The normalized form of <paramref name="path"/>.</summary>
    /// <exception cref="InvalidDatasetPathException">The path breaks the rule (400).</exception>
    public string Normalize(string? path, string? subject = null)
    {
        var result = Check(path);
        return result.Path ?? throw new InvalidDatasetPathException(path, result.Rejected!, subject);
    }

    /// <summary>
    /// The checked path of a request value (<see cref="DatasetPath"/>): null when nothing was sent (null or
    /// blank: the caller's "required" rule or default decides), the normalized path otherwise.
    /// </summary>
    /// <exception cref="InvalidDatasetPathException">The path breaks the rule (400).</exception>
    public DatasetPath? Parse(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : new DatasetPath(Normalize(path));

    /// <summary>
    /// Whether <paramref name="path"/> is inside a folder the Registry or libddb owns: a ".ddb"
    /// segment at any depth, or ".uploads" as the first segment. For paths the Registry produced
    /// itself (a walk of the dataset folder); a received path goes through <see cref="Normalize"/>.
    /// </summary>
    public bool IsReserved(string path) => Check(path).Rejected == EntryPathRejection.Reserved;

    /// <summary>
    /// A path received in a request names an entry from the dataset root: one leading '/' is dropped
    /// ("/img.jpg" names "img.jpg"), then <see cref="Normalize"/>. The one seam of request paths
    /// (<c>IDatasetAccessResolver</c>).
    /// </summary>
    /// <exception cref="InvalidDatasetPathException">The path breaks the rule (400).</exception>
    public string NormalizeRequestPath(string? path) =>
        Normalize(path != null && path.StartsWith('/') ? path[1..] : path);

    /// <summary>
    /// A destination folder of a request: "" (the dataset root) when it is null or blank, otherwise
    /// trimmed, one leading '/' dropped and <see cref="Normalize"/>d.
    /// </summary>
    /// <exception cref="InvalidDatasetPathException">The folder breaks the rule (400).</exception>
    public string NormalizeFolder(string? folder)
    {
        var trimmed = folder?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed == "/")
            return string.Empty;
        return NormalizeRequestPath(trimmed);
    }

    /// <summary>
    /// <paramref name="relative"/> (normalized, or a single name) inside the normalized folder
    /// <paramref name="folder"/> ("" for the dataset root), checked as a whole: the result may be
    /// reserved even when neither part is (<c>""</c> + <c>".uploads/x"</c>).
    /// </summary>
    public NormalizedEntryPath CheckIn(string folder, string relative) =>
        Check(string.IsNullOrEmpty(folder) ? relative : folder + "/" + relative);

    /// <summary>
    /// The dataset-relative key of an archive entry name, relative to the extraction folder.
    /// Leading separators are dropped first (archive tools do the same with "/abs" members), then
    /// libddb's rule judges what is left, then every segment is sanitized
    /// (<see cref="FileImportPolicy.SanitizePathSegments"/>). The answer is the key, or a rejection
    /// the extraction skips: <see cref="EntryPathRejection.Empty"/> (a folder entry, a name made of
    /// separators, dots or invalid characters only) or <see cref="EntryPathRejection.Reserved"/> (a
    /// ".ddb" segment).
    /// </summary>
    /// <exception cref="InvalidDatasetPathException">The name is unsafe (zip-slip): a NUL byte, an
    /// invalid encoding, a drive, UNC or device prefix, or a ".." segment.</exception>
    public NormalizedEntryPath NormalizeArchiveKey(string? key)
    {
        var name = (key ?? string.Empty).Trim();
        if (NormalizedEntryPath.RejectUnmarshallable(name) is { } unsafeName)
            throw new InvalidDatasetPathException(key, unsafeName.Rejected!, "archive entry");

        name = name.TrimStart(Separators);
        if (name.Length == 0)
            return NormalizedEntryPath.Rejection(EntryPathRejection.Empty);

        var result = _ddb.NormalizePath(name);
        switch (result.Rejected)
        {
            case null:
                break;
            case EntryPathRejection.Empty or EntryPathRejection.Reserved:
                return result;
            default:
                throw new InvalidDatasetPathException(key, result.Rejected, "archive entry");
        }

        var sanitized = FileImportPolicy.SanitizePathSegments(result.Path);
        return sanitized.Length == 0
            ? NormalizedEntryPath.Rejection(EntryPathRejection.Empty)
            : NormalizedEntryPath.Accepted(sanitized);
    }

    /// <summary>
    /// The local file of the normalized dataset path <paramref name="normalizedPath"/> under
    /// <paramref name="datasetFolder"/>. The containment check is defence in depth: a normalized path
    /// cannot leave the folder (symbolic links are not resolved).
    /// </summary>
    /// <exception cref="InvalidDatasetPathException">The result is not inside the folder.</exception>
    public static string ToLocalPath(string datasetFolder, string normalizedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetFolder);
        ArgumentNullException.ThrowIfNull(normalizedPath);

        var local = Path.Combine(datasetFolder, normalizedPath.ToLocalSeparators());
        if (!CommonUtils.IsUnder(datasetFolder, local))
            throw new InvalidDatasetPathException(normalizedPath, EntryPathRejection.Traversal);
        return local;
    }

    /// <summary>
    /// The dataset paths of the files under <paramref name="datasetFolder"/>, normalized: what a walk
    /// of the folder finds that can be a dataset entry. Reserved folders (".ddb" at any depth, the
    /// root ".uploads" with its staging and quarantine files) are pruned, never entered.
    /// </summary>
    public IEnumerable<string> EnumerateFiles(IFileSystem fs, string datasetFolder)
    {
        ArgumentNullException.ThrowIfNull(fs);
        if (!fs.FolderExists(datasetFolder))
            yield break;

        var pending = new Stack<string>();
        pending.Push(datasetFolder);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            foreach (var file in fs.EnumerateFiles(folder))
            {
                if (Check(Path.GetRelativePath(datasetFolder, file).ToDdbPath()).Path is { } path)
                    yield return path;
            }

            foreach (var sub in fs.EnumerateDirectories(folder))
            {
                if (!IsReserved(Path.GetRelativePath(datasetFolder, sub).ToDdbPath()))
                    pending.Push(sub);
            }
        }
    }

    /// <summary>
    /// The local files of the dataset paths of <paramref name="items"/>, received from an untrusted
    /// source (a remote registry listing), under <paramref name="datasetFolder"/>. Reserved paths are
    /// skipped. Every path is checked before the result is returned, so one unsafe path fails the
    /// whole batch before anything is written.
    /// </summary>
    /// <exception cref="InvalidDatasetPathException">A path is empty, unsafe or absolute.</exception>
    public IReadOnlyList<(T Item, string LocalPath)> ResolveUntrustedPaths<T>(string datasetFolder,
        IEnumerable<T> items, Func<T, string> relativePath)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(relativePath);

        var resolved = new List<(T Item, string LocalPath)>();
        foreach (var item in items)
        {
            var path = relativePath(item);
            var result = Check(path);
            if (result.Rejected == EntryPathRejection.Reserved)
                continue;
            if (result.Path == null)
                throw new InvalidDatasetPathException(path, result.Rejected!);

            resolved.Add((item, ToLocalPath(datasetFolder, result.Path)));
        }

        return resolved;
    }
}
