using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Stateless ZIP archive builder shared by the legacy download streaming path and
/// the <c>bulk-download</c> heavy tool. Centralizes the path-expansion
/// and entry-writing logic that previously lived in <c>ObjectsManager.GetFilePaths</c>
/// and <c>FileStreamDescriptor.AddFilesToZip</c>.
/// </summary>
public sealed class ZipArchiveBuilder : IZipArchiveBuilder
{
    public (string[] files, string[] folders, bool includeDdb) ExpandPaths(IDDB ddb, string[]? paths)
    {
        string[] files;
        string[] folders;
        var includeDdb = false;

        if (paths is { Length: > 0 })
        {
            var tempFiles = new List<string>();
            var tempFolders = new List<string>();

            foreach (var path in paths)
            {
                var entry = ddb.GetEntry(path);

                if (entry == null)
                    throw new InvalidOperationException($"Path '{path}' not found in ddb, cannot continue");

                if (entry.Type == EntryType.Directory)
                {
                    // Recursive mode: the paths could contain nested folders that we need to expand.
                    var items = ddb.FindSummaries(EntryQuery.Under(path));

                    tempFolders.Add(path);
                    tempFiles.AddRange(items.Where(item => item.Type != EntryType.Directory)
                        .Select(item => item.Path));
                    tempFolders.AddRange(items.Where(item => item.Type == EntryType.Directory)
                        .Select(item => item.Path));
                }
                else
                {
                    tempFiles.Add(path);
                }
            }

            // Get rid of possible duplicates and sort
            files = [.. tempFiles.Distinct().OrderBy(item => item)];
            folders = [.. tempFolders.Distinct().OrderBy(item => item)];
        }
        else
        {
            var entries = ddb.FindSummaries(EntryQuery.All);

            if (entries.Count == 0)
                throw new InvalidOperationException("Ddb is empty, what should I get?");

            // Select everything and sort
            files =
            [
                .. entries
                    .Where(entry => entry.Type != EntryType.Directory)
                    .Select(entry => entry.Path)
                    .OrderBy(path => path)
            ];

            folders =
            [
                .. entries
                    .Where(entry => entry.Type == EntryType.Directory)
                    .Select(entry => entry.Path)
                    .OrderBy(path => path)
            ];

            // We include the ddb folder only when asked for the entire dataset
            includeDdb = true;
        }

        return (files, folders, includeDdb);
    }

    public Task WriteZipAsync(IDDB ddb, string[] files, string[] folders, bool includeDdb,
        Stream output, IProgress<long>? progress = null, CancellationToken ct = default) =>
        WriteArchiveAsync(output, archive => WriteEntriesAsync(archive, ddb, files, folders, includeDdb, progress, ct),
            ct);

    public Task WriteFolderZipAsync(string folder, Stream output, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        return WriteArchiveAsync(output, archive => archive.CreateEntriesFromFolderAsync(folder, ct: ct), ct);
    }

    // Written entry by entry, asynchronously and without seeking, so output can be a response
    // body: the first bytes leave while later files are still being read, and
    // memory stays at the copy buffer whatever the size of the selection
    private static async Task WriteArchiveAsync(Stream output, Func<ZipArchive, Task> write, CancellationToken ct)
    {
        await using var sink = new SyncWriteBufferingStream(output);
        await using (var archive = await ZipArchive.CreateAsync(sink, ZipArchiveMode.Create, leaveOpen: true,
                         entryNameEncoding: null, ct))
        {
            await write(archive);
        }
        // The archive's last records (data descriptor, central directory) may still be pending
        await sink.FlushAsync(ct);
    }

    private static async Task WriteEntriesAsync(ZipArchive archive, IDDB ddb, string[] files, string[] folders,
        bool includeDdb, IProgress<long>? progress, CancellationToken ct)
    {
        long written = 0;
        var buffer = new byte[81920];

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();

            var entry = archive.CreateEntry(path, CommonUtils.GetCompressionLevel(path));
            await using var entryStream = await entry.OpenAsync(ct);

            var localPath = ddb.GetLocalPath(path);
            await using var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);

            int read;
            while ((read = await fileStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                await entryStream.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
                progress?.Report(written);
            }
        }

        // Folders are handled separately: empty folders would otherwise not be
        // included in the archive.
        if (folders != null)
        {
            foreach (var folder in folders)
                archive.CreateEntry(folder + "/");
        }

        // Include the .ddb database folder when archiving the whole dataset (without the folders of
        // derived data, the builds), reproducing the legacy whole-dataset archive.
        if (includeDdb)
        {
            // Index writes leave their changes in the WAL: they go into the database file first.
            // The WAL is archived with it, so a checkpoint that another connection kept from
            // completing loses nothing. The WAL's shared-memory index (-shm) is local to the
            // connections of this machine and is rebuilt on open.
            ddb.Checkpoint();
            var database = Path.Combine(ddb.DatasetFolderPath, IDDB.DatabaseFolderName);
            await archive.CreateEntryFromAnyAsync(database, string.Empty,
                [.. ddb.DerivedFolderPaths, Path.Combine(database, IDDB.DatabaseFileName + "-shm")], ct);
        }
    }
}
