#nullable enable
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
/// the <c>bulk-download</c> heavy tool (spec §A.0). Centralizes the path-expansion
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
                    var items = ddb.Search(path, true)?.ToArray();

                    if (items == null)
                        throw new InvalidOperationException("Ddb is empty, what should I get?");

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
            files = tempFiles.Distinct().OrderBy(item => item).ToArray();
            folders = tempFolders.Distinct().OrderBy(item => item).ToArray();
        }
        else
        {
            var entries = ddb.Search(null, true)?.ToArray();

            if (entries == null || entries.Length == 0)
                throw new InvalidOperationException("Ddb is empty, what should I get?");

            // Select everything and sort
            files = entries
                .Where(entry => entry.Type != EntryType.Directory)
                .Select(entry => entry.Path)
                .OrderBy(path => path)
                .ToArray();

            folders = entries
                .Where(entry => entry.Type == EntryType.Directory)
                .Select(entry => entry.Path)
                .OrderBy(path => path)
                .ToArray();

            // We include the ddb folder only when asked for the entire dataset
            includeDdb = true;
        }

        return (files, folders, includeDdb);
    }

    public async Task WriteZipAsync(IDDB ddb, string[] files, string[] folders, bool includeDdb,
        Stream output, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        long written = 0;
        var buffer = new byte[81920];

        // leaveOpen so the caller (legacy memory/temp-file path) can seek and copy
        // the completed archive to the response stream afterwards.
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();

            var entry = archive.CreateEntry(path, CommonUtils.GetCompressionLevel(path));
            await using var entryStream = await entry.OpenAsync();

            var localPath = ddb.GetLocalPath(path);
            await using var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read);

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

        // Include the .ddb database folder when archiving the whole dataset (excludes
        // the build folder), reproducing the legacy whole-dataset archive.
        if (includeDdb)
        {
            archive.CreateEntryFromAny(
                Path.Combine(ddb.DatasetFolderPath, IDDB.DatabaseFolderName),
                string.Empty, [ddb.BuildFolderPath]);
        }
    }
}
