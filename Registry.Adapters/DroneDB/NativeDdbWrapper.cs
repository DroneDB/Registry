#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Ports;
using Registry.Ports.DroneDB;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// <see cref="IDdbWrapper"/> over the libddb C ABI 1.1 (<see cref="NativeDdb"/>). Each member validates
/// its arguments, then makes one call through <see cref="NativeCall"/>, which owns the error, free and
/// deserialize envelope. The process lifecycle (configuration, initialization, shutdown) is
/// <see cref="NativeDdbRuntime"/>'s; the ABI is checked once at startup (<see cref="DdbAbi"/>).
/// </summary>
public class NativeDdbWrapper : IDdbWrapper
{
    // DDBAddWithOptions policy, not caller-configurable: never recurse (a recursive native walk would
    // report paths the caller never provided, breaking the completeness contract) and keep the native
    // conflict-retry budget at its default of 2
    private const int MaxConflictRetries = 2;

    // The entry types never change in a process
    private static readonly Lazy<IReadOnlyList<EntryTypeInfo>> EntryTypes = new(() =>
        NativeCall.Invoke<List<EntryTypeInfo>>(nameof(NativeDdb.DDBGetEntryTypes),
            (out IntPtr output) => NativeDdb.DDBGetEntryTypes(out output)));

    public string TileMimeType => "image/png";
    public string ThumbnailMimeType => "image/webp";

    public string GetVersion()
    {
        var version = Marshal.PtrToStringUTF8(NativeDdb.DDBGetVersion());
        return string.IsNullOrWhiteSpace(version) ? throw new DdbException("Unable to get version") : version;
    }

    // ---------------------------------------------------------------- index

    public string Init(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBInit),
            (out IntPtr output) => NativeDdb.DDBInit(directory, out output));
    }

    public List<Entry> Add(string ddbPath, string path, bool recursive = false) => Add(ddbPath, [path], recursive);

    public List<Entry> Add(string ddbPath, string[] paths, bool recursive = false)
    {
        ArgumentNullException.ThrowIfNull(ddbPath);
        var normalized = NormalizeSeparators(paths);
        return NativeCall.Invoke<List<Entry>>(nameof(NativeDdb.DDBAdd),
            (out IntPtr output) => NativeDdb.DDBAdd(ddbPath, normalized, normalized.Length, out output, recursive));
    }

    public BatchAddResult AddWithOptions(string ddbPath, string[] paths, bool stopOnError = false)
    {
        ArgumentNullException.ThrowIfNull(ddbPath);
        var normalized = NormalizeSeparators(paths);
        var options = DdbAddOptions.Create(false, stopOnError, MaxConflictRetries);
        return NativeCall.Invoke<BatchAddResult>(nameof(NativeDdb.DDBAddWithOptions),
            (out IntPtr output) =>
                NativeDdb.DDBAddWithOptions(ddbPath, normalized, normalized.Length, in options, out output));
    }

    public unsafe BatchAddResult AddWithHints(string ddbPath, IReadOnlyList<AddItem> files, bool stopOnError = false)
    {
        ArgumentNullException.ThrowIfNull(ddbPath);
        ArgumentNullException.ThrowIfNull(files);

        if (files.All(f => f.Hint == null))
            return AddWithOptions(ddbPath, files.Select(f => f.Path).ToArray(), stopOnError);

        // DDBAddHint holds UTF-8 pointers owned by the caller for the duration of the call
        var hints = new DdbAddHint[files.Count];
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var hint = files[i].Hint;
                hints[i] = new DdbAddHint
                {
                    Path = (IntPtr)Utf8StringMarshaller.ConvertToUnmanaged(NormalizeSeparators(files[i].Path)),
                    Sha256 = (IntPtr)Utf8StringMarshaller.ConvertToUnmanaged(hint?.Sha256 ?? string.Empty),
                    Size = hint?.Size ?? -1,
                    Mtime = hint == null ? 0 : (hint.ModifiedUtc.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds
                };
            }

            var options = DdbAddOptions.Create(false, stopOnError, MaxConflictRetries);
            fixed (DdbAddHint* pointer = hints)
            {
                var first = (IntPtr)pointer;
                return NativeCall.Invoke<BatchAddResult>(nameof(NativeDdb.DDBAddWithHints),
                    (out IntPtr output) =>
                        NativeDdb.DDBAddWithHints(ddbPath, (DdbAddHint*)first, hints.Length, in options, out output));
            }
        }
        finally
        {
            foreach (var hint in hints)
            {
                Utf8StringMarshaller.Free((byte*)hint.Path);
                Utf8StringMarshaller.Free((byte*)hint.Sha256);
            }
        }
    }

    public void Remove(string ddbPath, string path) => Remove(ddbPath, [path]);

    public void Remove(string ddbPath, string[] paths)
    {
        ArgumentNullException.ThrowIfNull(ddbPath);
        var normalized = NormalizeSeparators(paths);
        NativeCall.Invoke(nameof(NativeDdb.DDBRemove),
            () => NativeDdb.DDBRemove(ddbPath, normalized, normalized.Length));
    }

    public List<Entry> Info(string path, bool recursive = false, int maxRecursionDepth = 0, bool withHash = false) =>
        Info([path], recursive, maxRecursionDepth, withHash);

    public List<Entry> Info(string[] paths, bool recursive = false, int maxRecursionDepth = 0, bool withHash = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return NativeCall.Invoke<List<Entry>>(nameof(NativeDdb.DDBInfo),
            (out IntPtr output) => NativeDdb.DDBInfo(paths, paths.Length, out output, "json", recursive,
                maxRecursionDepth, "auto", withHash, true));
    }

    public int ReleaseConnections(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var closed = 0;
        NativeCall.Invoke(nameof(NativeDdb.DDBReleaseConnections),
            () => NativeDdb.DDBReleaseConnections(path, out closed));
        return closed;
    }

    public bool Checkpoint(string ddbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ddbPath);
        var complete = 0;
        NativeCall.Invoke(nameof(NativeDdb.DDBCheckpoint), () => NativeDdb.DDBCheckpoint(ddbPath, out complete));
        return complete != 0;
    }

    public Entry? GetEntry(string ddbPath, string path)
    {
        ArgumentNullException.ThrowIfNull(ddbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var relative = NormalizeSeparators(path);
        return NativeCall.InvokeOrNotFound<Entry>(nameof(NativeDdb.DDBGet),
            (out IntPtr output) => NativeDdb.DDBGet(ddbPath, relative, out output));
    }

    public List<Entry> ListEntries(string ddbPath, EntryQuery query) => ListEntries<Entry>(ddbPath, query, true);

    public List<EntrySummary> ListEntrySummaries(string ddbPath, EntryQuery query) =>
        ListEntries<EntrySummary>(ddbPath, query, false);

    private static List<T> ListEntries<T>(string ddbPath, EntryQuery query, bool withDetails)
    {
        ArgumentNullException.ThrowIfNull(ddbPath);
        ArgumentNullException.ThrowIfNull(query);

        // Native type names are the enum names (typeFromHuman ignores case)
        var types = query.Types is { Count: > 0 }
            ? string.Join(',', query.Types.Distinct().Select(t => t.ToString().ToLowerInvariant()))
            : string.Empty;
        var folder = NormalizeSeparators(query.Path ?? string.Empty);
        var namePattern = query.NamePattern ?? string.Empty;

        return NativeCall.Invoke<List<T>>(nameof(NativeDdb.DDBListEntries),
            (out IntPtr output) => NativeDdb.DDBListEntries(ddbPath, folder, query.Recursive, namePattern, types,
                withDetails, out output));
    }

    public List<Entry> List(string ddbPath, string path, bool recursive = false, int maxRecursionDepth = 0) =>
        List(ddbPath, [path], recursive, maxRecursionDepth);

    public List<Entry> List(string ddbPath, string[] paths, bool recursive = false, int maxRecursionDepth = 0)
    {
        ArgumentNullException.ThrowIfNull(ddbPath);
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Length == 0)
            throw new ArgumentException("Paths is empty", nameof(paths));

        var normalized = NormalizeSeparators(paths);
        return NativeCall.Invoke<List<Entry>>(nameof(NativeDdb.DDBList),
            (out IntPtr output) => NativeDdb.DDBList(ddbPath, normalized, normalized.Length, out output, "json",
                recursive, maxRecursionDepth));
    }

    public void AppendPassword(string ddbPath, string password) =>
        NativeCall.Invoke(nameof(NativeDdb.DDBAppendPassword), () => NativeDdb.DDBAppendPassword(ddbPath, password));

    public bool VerifyPassword(string ddbPath, string password)
    {
        var verified = false;
        NativeCall.Invoke(nameof(NativeDdb.DDBVerifyPassword),
            () => NativeDdb.DDBVerifyPassword(ddbPath, password, out verified));
        return verified;
    }

    public void ClearPasswords(string ddbPath) =>
        NativeCall.Invoke(nameof(NativeDdb.DDBClearPasswords), () => NativeDdb.DDBClearPasswords(ddbPath));

    public Dictionary<string, object> ChangeAttributes(string ddbPath, Dictionary<string, object> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        var json = JsonConvert.SerializeObject(attributes);
        return NativeCall.Invoke<Dictionary<string, object>>(nameof(NativeDdb.DDBChattr),
            (out IntPtr output) => NativeDdb.DDBChattr(ddbPath, json, out output));
    }

    public Dictionary<string, object> GetAttributes(string ddbPath) =>
        ChangeAttributes(ddbPath, new Dictionary<string, object>());

    public void SetTag(string ddbPath, string newTag)
    {
        ArgumentNullException.ThrowIfNull(ddbPath);
        ArgumentNullException.ThrowIfNull(newTag);
        NativeCall.Invoke(nameof(NativeDdb.DDBSetTag), () => NativeDdb.DDBSetTag(ddbPath, newTag));
    }

    public string? GetTag(string ddbPath)
    {
        ArgumentNullException.ThrowIfNull(ddbPath);
        var tag = NativeCall.InvokeString(nameof(NativeDdb.DDBGetTag),
            (out IntPtr output) => NativeDdb.DDBGetTag(ddbPath, out output));
        return string.IsNullOrWhiteSpace(tag) ? null : tag;
    }

    public Stamp GetStamp(string ddbPath)
    {
        ArgumentNullException.ThrowIfNull(ddbPath);
        return NativeCall.Invoke<Stamp>(nameof(NativeDdb.DDBGetStamp),
            (out IntPtr output) => NativeDdb.DDBGetStamp(ddbPath, out output));
    }

    public string GetStampChecksum(string ddbPath)
    {
        ArgumentNullException.ThrowIfNull(ddbPath);
        return NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBGetStampChecksum),
            (out IntPtr output) => NativeDdb.DDBGetStampChecksum(ddbPath, out output));
    }

    public Delta Delta(string ddbPath, string ddbTarget) => Delta(GetStamp(ddbPath), GetStamp(ddbTarget));

    public Delta Delta(Stamp source, Stamp target)
    {
        var sourceJson = JsonConvert.SerializeObject(source);
        var targetJson = JsonConvert.SerializeObject(target);
        return NativeCall.Invoke<Delta>(nameof(NativeDdb.DDBDelta),
            (out IntPtr output) => NativeDdb.DDBDelta(sourceJson, targetJson, out output, "json"));
    }

    public List<string> ApplyDelta(Delta delta, string sourcePath, string ddbPath, MergeStrategy mergeStrategy,
        string? sourceMetaDump = null)
    {
        var deltaJson = JsonConvert.SerializeObject(delta);
        var metaDump = sourceMetaDump ?? "[]";
        return NativeCall.Invoke<List<string>>(nameof(NativeDdb.DDBApplyDelta),
            (out IntPtr output) => NativeDdb.DDBApplyDelta(deltaJson, sourcePath, ddbPath, (int)mergeStrategy,
                metaDump, out output));
    }

    public Dictionary<string, bool> ComputeDeltaLocals(Delta delta, string ddbPath, string hlDestFolder = "")
    {
        var deltaJson = JsonConvert.SerializeObject(delta);
        return NativeCall.Invoke<Dictionary<string, bool>>(nameof(NativeDdb.DDBComputeDeltaLocals),
            (out IntPtr output) => NativeDdb.DDBComputeDeltaLocals(deltaJson, ddbPath, hlDestFolder, out output));
    }

    public void MoveEntry(string ddbPath, string source, string dest)
    {
        var from = NormalizeSeparators(source);
        var to = NormalizeSeparators(dest);
        NativeCall.Invoke(nameof(NativeDdb.DDBMoveEntry), () => NativeDdb.DDBMoveEntry(ddbPath, from, to));
    }

    public List<RescanResult> RescanIndex(string ddbPath, RescanOptions? options = null)
    {
        options ??= new RescanOptions();
        var types = options.Types ?? string.Empty;
        var extensions = options.Extensions;

        // DDBRescanEx only with an extension filter: the type-only rescan stays the call it always was
        return string.IsNullOrWhiteSpace(extensions)
            ? NativeCall.Invoke<List<RescanResult>>(nameof(NativeDdb.DDBRescan),
                (out IntPtr output) => NativeDdb.DDBRescan(ddbPath, out output, types, options.StopOnError))
            : NativeCall.Invoke<List<RescanResult>>(nameof(NativeDdb.DDBRescanEx),
                (out IntPtr output) =>
                    NativeDdb.DDBRescanEx(ddbPath, out output, types, extensions, options.StopOnError));
    }

    // ---------------------------------------------------------------- build

    public void Build(string ddbPath, string? source = null, string? dest = null, bool force = false,
        bool pendingOnly = false)
    {
        ArgumentNullException.ThrowIfNull(ddbPath);
        var result = NativeDdb.DDBBuild(ddbPath, source == null ? null : NormalizeSeparators(source),
            dest == null ? null : NormalizeSeparators(dest), force, pendingOnly);

        // BuildDependencyMissing: a dependency is not indexed yet, so the build is deferred (a
        // pending marker), not failed. BuildInProgress and Busy surface as typed exceptions, which
        // the Hangfire retry policies retry with backoff.
        if (result != DdbResult.BuildDependencyMissing)
            NativeCall.Check(result, nameof(NativeDdb.DDBBuild));
    }

    public EntryBuildArtifacts[] GetBuildArtifacts(string ddbPath, string[] paths)
    {
        ArgumentNullException.ThrowIfNull(ddbPath);
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Length == 0)
            return [];

        var normalized = NormalizeSeparators(paths);
        return NativeCall.Invoke<EntryBuildArtifacts[]>(nameof(NativeDdb.DDBGetBuildArtifacts),
            (out IntPtr output) => NativeDdb.DDBGetBuildArtifacts(ddbPath, normalized, normalized.Length, out output));
    }

    public string? ResolveBuildFile(string ddbPath, string hash, string path)
    {
        ArgumentNullException.ThrowIfNull(ddbPath);
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(path);
        return NativeCall.InvokeStringOrNotFound(nameof(NativeDdb.DDBResolveBuildFile),
            (out IntPtr output) => NativeDdb.DDBResolveBuildFile(ddbPath, hash, path, out output));
    }

    public IReadOnlyList<EntryTypeInfo> GetEntryTypes() => EntryTypes.Value;

    public NormalizedEntryPath NormalizePath(string path, IReadOnlyCollection<string>? reservedFirstSegments = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        // The UTF-8 marshaller would pass "a\0b" as "a": answer for the path given, not a shorter one
        if (NormalizedEntryPath.RejectUnmarshallable(path) is { } rejected)
            return rejected;

        var reservedJson = reservedFirstSegments is { Count: > 0 }
            ? JsonConvert.SerializeObject(reservedFirstSegments)
            : null;
        try
        {
            return NativeCall.Invoke<NormalizedEntryPath>(nameof(NativeDdb.DDBNormalizePath),
                (out IntPtr output) => NativeDdb.DDBNormalizePath(path, reservedJson, out output));
        }
        catch (DdbInvalidArgumentsException ex)
        {
            // The only invalid argument left is the reserved list (a name with a separator, "..")
            throw new ArgumentException(ex.Message, nameof(reservedFirstSegments), ex);
        }
    }

    public CopiedBuildArtifacts CopyBuildArtifacts(string sourceDdbPath, string destinationDdbPath, string hash)
    {
        ArgumentNullException.ThrowIfNull(sourceDdbPath);
        ArgumentNullException.ThrowIfNull(destinationDdbPath);
        ArgumentNullException.ThrowIfNull(hash);
        return NativeCall.Invoke<CopiedBuildArtifacts>(nameof(NativeDdb.DDBCopyBuildArtifacts),
            (out IntPtr output) =>
                NativeDdb.DDBCopyBuildArtifacts(sourceDdbPath, destinationDdbPath, hash, out output));
    }

    public bool IsBuildPending(string ddbPath)
    {
        ArgumentNullException.ThrowIfNull(ddbPath);
        var pending = false;
        NativeCall.Invoke(nameof(NativeDdb.DDBIsBuildPending), () => NativeDdb.DDBIsBuildPending(ddbPath, out pending));
        return pending;
    }

    public PendingBuildInfo[] GetPendingBuildInfo(string ddbPath)
    {
        ArgumentNullException.ThrowIfNull(ddbPath);
        return NativeCall.Invoke<PendingBuildInfo[]>(nameof(NativeDdb.DDBGetPendingBuildInfo),
            (out IntPtr output) => NativeDdb.DDBGetPendingBuildInfo(ddbPath, out output));
    }

    public DdbCleanupResult Cleanup(string ddbPath) =>
        NativeCall.Invoke<DdbCleanupResult>(nameof(NativeDdb.DDBCleanup),
            (out IntPtr output) => NativeDdb.DDBCleanup(ddbPath, out output));

    // ---------------------------------------------------------------- meta

    public Meta MetaAdd(string ddbPath, string key, string data, string? path = null) =>
        NativeCall.Invoke<Meta>(nameof(NativeDdb.DDBMetaAdd),
            (out IntPtr output) => NativeDdb.DDBMetaAdd(ddbPath, path ?? string.Empty, key, data, out output));

    public Meta MetaSet(string ddbPath, string key, string data, string? path = null) =>
        NativeCall.Invoke<Meta>(nameof(NativeDdb.DDBMetaSet),
            (out IntPtr output) => NativeDdb.DDBMetaSet(ddbPath, path ?? string.Empty, key, data, out output));

    public int MetaRemove(string ddbPath, string id) =>
        Removed(nameof(NativeDdb.DDBMetaRemove),
            NativeCall.Invoke<JObject>(nameof(NativeDdb.DDBMetaRemove),
                (out IntPtr output) => NativeDdb.DDBMetaRemove(ddbPath, id, out output)));

    public string? MetaGet(string ddbPath, string key, string? path = null) =>
        NativeCall.InvokeString(nameof(NativeDdb.DDBMetaGet),
            (out IntPtr output) => NativeDdb.DDBMetaGet(ddbPath, path ?? string.Empty, key, out output));

    public int MetaUnset(string ddbPath, string key, string? path = null) =>
        Removed(nameof(NativeDdb.DDBMetaUnset),
            NativeCall.Invoke<JObject>(nameof(NativeDdb.DDBMetaUnset),
                (out IntPtr output) => NativeDdb.DDBMetaUnset(ddbPath, path ?? string.Empty, key, out output)));

    public List<MetaListItem> MetaList(string ddbPath, string? path = null) =>
        NativeCall.Invoke<List<MetaListItem>>(nameof(NativeDdb.DDBMetaList),
            (out IntPtr output) => NativeDdb.DDBMetaList(ddbPath, path ?? string.Empty, out output));

    public List<MetaDump> MetaDump(string ddbPath, string? ids = null) =>
        NativeCall.Invoke<List<MetaDump>>(nameof(NativeDdb.DDBMetaDump),
            (out IntPtr output) => NativeDdb.DDBMetaDump(ddbPath, ids ?? "[]", out output));

    // The meta-count output: {"removed": n}
    private static int Removed(string operation, JObject output) =>
        output.TryGetValue("removed", out var removed)
            ? removed.ToObject<int>()
            : throw new DdbException($"{operation} returned no 'removed' count: {output.ToString(Formatting.None)}");

    // ---------------------------------------------------------------- STAC

    public JToken Stac(string ddbPath, string? entry, string stacCollectionRoot, string id, string stacCatalogRoot) =>
        NativeCall.Invoke<JToken>(nameof(NativeDdb.DDBStac),
            (out IntPtr output) => NativeDdb.DDBStac(ddbPath, entry ?? string.Empty, stacCollectionRoot, id,
                stacCatalogRoot, out output));

    public JToken StacItemCollection(string ddbPath, string stacCollectionRoot, string id, string stacCatalogRoot,
        string? bbox, string? datetime, int limit, int offset) =>
        NativeCall.Invoke<JToken>(nameof(NativeDdb.DDBStacItemCollection),
            (out IntPtr output) => NativeDdb.DDBStacItemCollection(ddbPath, stacCollectionRoot, id, stacCatalogRoot,
                bbox ?? string.Empty, datetime ?? string.Empty, limit, offset, out output));

    // ---------------------------------------------------------------- thumbnails and tiles

    public void GenerateThumbnail(string filePath, int size, string destPath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(destPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        RequireFile(filePath);

        NativeCall.Invoke(nameof(NativeDdb.DDBGenerateThumbnail),
            () => NativeDdb.DDBGenerateThumbnail(filePath, size, destPath),
            $"file: '{filePath}', size: {size}, dest: '{destPath}'");
    }

    public byte[] GenerateThumbnail(string filePath, int size)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        RequireFile(filePath);

        return NativeCall.InvokeBuffer(nameof(NativeDdb.DDBGenerateMemoryThumbnail),
            (out IntPtr buffer, out int length) =>
                NativeDdb.DDBGenerateMemoryThumbnail(filePath, size, out buffer, out length),
            $"file: '{filePath}', size: {size}");
    }

    public byte[] GenerateThumbnailEx(string filePath, int size, string? preset = null,
        string? bands = null, string? formula = null, string? bandFilter = null,
        string? colormap = null, string? rescale = null)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        RequireFile(filePath);

        return NativeCall.InvokeBuffer(nameof(NativeDdb.DDBGenerateMemoryThumbnailEx),
            (out IntPtr buffer, out int length) => NativeDdb.DDBGenerateMemoryThumbnailEx(filePath, size, preset,
                bands, formula, bandFilter, colormap, rescale, out buffer, out length),
            $"file: '{filePath}', size: {size}");
    }

    public string GenerateTile(string inputPath, int tz, int tx, int ty, int tileSize, bool tms,
        bool forceRecreate = false)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        return NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBTile),
            (out IntPtr output) => NativeDdb.DDBTile(inputPath, tz, tx, ty, out output, tileSize, tms, forceRecreate));
    }

    public byte[] GenerateMemoryTile(string inputPath, int tz, int tx, int ty, int tileSize, bool tms,
        bool forceRecreate = false, string inputPathHash = "")
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        return NativeCall.InvokeBuffer(nameof(NativeDdb.DDBMemoryTile),
            (out IntPtr buffer, out int length) => NativeDdb.DDBMemoryTile(inputPath, tz, tx, ty, out buffer,
                out length, tileSize, tms, forceRecreate, inputPathHash ?? string.Empty));
    }

    public byte[] GenerateMemoryTile(string inputPath, int tz, int tx, int ty, int tileSize, bool tms,
        bool forceRecreate, string inputPathHash, string outputFormat)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        return NativeCall.InvokeBuffer(nameof(NativeDdb.DDBMemoryTileFmt),
            (out IntPtr buffer, out int length) => NativeDdb.DDBMemoryTileFmt(inputPath, tz, tx, ty, out buffer,
                out length, tileSize, tms, forceRecreate, inputPathHash ?? string.Empty, outputFormat ?? "png"));
    }

    public byte[] GenerateMemoryTileEx(string inputPath, int tz, int tx, int ty,
        int tileSize, bool tms, bool forceRecreate, string inputPathHash,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        return NativeCall.InvokeBuffer(nameof(NativeDdb.DDBMemoryTileEx),
            (out IntPtr buffer, out int length) => NativeDdb.DDBMemoryTileEx(inputPath, tz, tx, ty, tileSize, tms,
                forceRecreate, inputPathHash ?? string.Empty, preset, bands, formula, bandFilter, colormap, rescale,
                out buffer, out length));
    }

    // ---------------------------------------------------------------- rasters

    public string GetRasterInfo(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBGetRasterInfo),
            (out IntPtr output) => NativeDdb.DDBGetRasterInfo(path, out output));
    }

    public string GetRasterMetadata(string path, string? formula = null, string? bandFilter = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBGetRasterMetadata),
            (out IntPtr output) => NativeDdb.DDBGetRasterMetadata(path, formula, bandFilter, out output));
    }

    public string ValidateMergeMultispectral(string[] paths)
    {
        RequirePaths(paths);
        return NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBValidateMergeMultispectral),
            (out IntPtr output) => NativeDdb.DDBValidateMergeMultispectral(paths, paths.Length, out output));
    }

    public byte[] PreviewMergeMultispectral(string[] paths, string? previewBands = null, int thumbSize = 512)
    {
        RequirePaths(paths);
        return NativeCall.InvokeBuffer(nameof(NativeDdb.DDBPreviewMergeMultispectral),
            (out IntPtr buffer, out int length) => NativeDdb.DDBPreviewMergeMultispectral(paths, paths.Length,
                previewBands, thumbSize, out buffer, out length));
    }

    public void MergeMultispectral(string[] paths, string outputCog)
    {
        RequirePaths(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputCog);
        NativeCall.Invoke(nameof(NativeDdb.DDBMergeMultispectral),
            () => NativeDdb.DDBMergeMultispectral(paths, paths.Length, outputCog));
    }

    public string ValidateAlignRaster(string sourcePath, string referencePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(referencePath);
        return NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBValidateAlignRaster),
            (out IntPtr output) => NativeDdb.DDBValidateAlignRaster(sourcePath, referencePath, out output));
    }

    public string AlignRaster(string sourcePath, string referencePath, string outputPath, string mode = "similarity")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(referencePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        return NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBAlignRaster),
            (out IntPtr output) => NativeDdb.DDBAlignRaster(sourcePath, referencePath, outputPath, mode, out output));
    }

    public void ExportRaster(string inputPath, string outputPath,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        NativeCall.Invoke(nameof(NativeDdb.DDBExportRaster),
            () => NativeDdb.DDBExportRaster(inputPath, outputPath, preset, bands, formula, bandFilter, colormap,
                rescale));
    }

    /// <summary>
    /// Export raster with visualization params applied as GeoTIFF using the block-windowed
    /// implementation (bounded peak memory), with progress reporting and cooperative cancellation.
    /// </summary>
    /// <param name="tileSize">Tile size in pixels for windowed processing; 0 = auto.</param>
    /// <param name="progress">Optional callback invoked with (fraction 0..1, phase). May be null.</param>
    /// <param name="cancellationToken">Cancels the operation cooperatively; raises <see cref="DdbCanceledException"/>.</param>
    public unsafe void ExportRaster(string inputPath, string outputPath,
        string? preset, string? bands, string? formula, string? bandFilter,
        string? colormap, string? rescale,
        int tileSize, Action<double, string?>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        // The bridge is only allocated when there is something to observe
        using var callback = NativeProgressCallback.Create(progress, cancellationToken);

        var result = NativeDdb.DDBExportRaster2(inputPath, outputPath, preset, bands, formula, bandFilter, colormap,
            rescale, tileSize, callback == null ? null : NativeProgressCallback.FunctionPointer,
            callback?.UserData ?? IntPtr.Zero);

        // An exception thrown by the observer canceled the call: it wins over the canceled result.
        // ThrowIfFaulted makes no libddb call, so the last error is still the export's.
        callback?.ThrowIfFaulted();
        NativeCall.Check(result, nameof(NativeDdb.DDBExportRaster2));
    }

    public string GetRasterValueInfo(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBGetRasterValueInfo),
            (out IntPtr output) => NativeDdb.DDBGetRasterValueInfo(path, out output));
    }

    public string GetRasterPointValue(string path, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(path);
        return NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBGetRasterPointValue),
            (out IntPtr output) => NativeDdb.DDBGetRasterPointValue(path, x, y, out output));
    }

    public string GetRasterAreaStats(string path, int x0, int y0, int x1, int y1)
    {
        ArgumentNullException.ThrowIfNull(path);
        return NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBGetRasterAreaStats),
            (out IntPtr output) => NativeDdb.DDBGetRasterAreaStats(path, x0, y0, x1, y1, out output));
    }

    public string GetRasterProfile(string path, string geoJsonLineString, int samples)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(geoJsonLineString);
        return NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBGetRasterProfile),
            (out IntPtr output) => NativeDdb.DDBGetRasterProfile(path, geoJsonLineString, samples, out output));
    }

    public string CalculateVolume(string path, string polygonGeoJson, string baseMethod, double flatElevation)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(polygonGeoJson);
        var method = baseMethod ?? string.Empty;
        return NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBCalculateVolume),
            (out IntPtr output) => NativeDdb.DDBCalculateVolume(path, polygonGeoJson, method, flatElevation,
                out output));
    }

    public string DetectStockpile(string path, double lat, double lon, double radiusMeters, float sensitivity)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!(radiusMeters > 0))
            throw new ArgumentException("radius must be positive", nameof(radiusMeters));

        return NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBDetectStockpile),
            (out IntPtr output) => NativeDdb.DDBDetectStockpile(path, lat, lon, radiusMeters, sensitivity, out output));
    }

    public string DetectAllStockpiles(string path, float sensitivity, double minAreaM2, int maxResults)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (sensitivity is < 0f or > 1f)
            throw new ArgumentException("sensitivity must be in [0,1]", nameof(sensitivity));
        if (minAreaM2 < 0.0)
            throw new ArgumentException("minAreaM2 must be >= 0", nameof(minAreaM2));
        if (maxResults <= 0)
            throw new ArgumentException("maxResults must be > 0", nameof(maxResults));

        return NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBDetectAllStockpiles),
            (out IntPtr output) =>
                NativeDdb.DDBDetectAllStockpiles(path, sensitivity, minAreaM2, maxResults, out output));
    }

    public string GenerateContours(string path,
                                   double? interval,
                                   int? count,
                                   double baseOffset = 0.0,
                                   double? minElev = null,
                                   double? maxElev = null,
                                   double simplifyTolerance = 0.0,
                                   int bandIndex = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!interval.HasValue && !count.HasValue)
            throw new ArgumentException("Either interval or count must be specified");
        if (interval is <= 0.0)
            throw new ArgumentException("interval must be > 0", nameof(interval));
        if (count is <= 0)
            throw new ArgumentException("count must be > 0", nameof(count));
        if (simplifyTolerance < 0.0)
            throw new ArgumentException("simplifyTolerance must be >= 0", nameof(simplifyTolerance));
        if (bandIndex <= 0)
            throw new ArgumentException("bandIndex must be > 0", nameof(bandIndex));

        // Native "unset" values: <= 0 for interval and count, NaN for the bounds
        var iv = interval ?? 0.0;
        var cnt = count ?? 0;
        var lo = minElev ?? double.NaN;
        var hi = maxElev ?? double.NaN;

        return NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBGenerateContours),
            (out IntPtr output) => NativeDdb.DDBGenerateContours(path, iv, cnt, baseOffset, lo, hi, simplifyTolerance,
                bandIndex, out output));
    }

    public void MaskBorders(string input, string output, int nearDist = 15, bool white = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        NativeCall.Invoke(nameof(NativeDdb.DDBMaskBorders), () => NativeDdb.DDBMaskBorders(input, output, nearDist, white));
    }

    // ---------------------------------------------------------------- OGC

    public byte[] RenderRasterRegion(string inputPath, double[] bbox, string bboxSrs,
                                     int width, int height, string format,
                                     int[]? bands = null, string? outputCrs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        RequireBbox(bbox);
        RequireSize(width, height);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);

        // Always the Ex entry point, so legacy and OGC WCS callers (band subset, output CRS) share one path
        var bandArray = bands ?? [];
        return NativeCall.InvokeBuffer(nameof(NativeDdb.DDBRenderRasterRegionEx),
            (out IntPtr buffer, out int length) => NativeDdb.DDBRenderRasterRegionEx(inputPath, bbox,
                bboxSrs ?? string.Empty, outputCrs ?? string.Empty, bandArray, bandArray.Length, width, height,
                format, out buffer, out length));
    }

    public byte[] RenderRasterIndex(string inputPath, string indexName, double[] bbox,
                                    string bboxSrs, int width, int height, string format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        RequireBbox(bbox);
        RequireSize(width, height);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);

        return NativeCall.InvokeBuffer(nameof(NativeDdb.DDBRenderRasterIndex),
            (out IntPtr buffer, out int length) => NativeDdb.DDBRenderRasterIndex(inputPath, indexName, bbox,
                bboxSrs ?? string.Empty, width, height, format, out buffer, out length));
    }

    public string QueryRasterPoint(string inputPath, double x, double y, string? srs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        return NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBQueryRasterPoint),
            (out IntPtr output) => NativeDdb.DDBQueryRasterPoint(inputPath, x, y, srs ?? string.Empty, out output));
    }

    public string QueryVector(string vectorPath, VectorQuery query)
    {
        ValidateVectorQuery(vectorPath, query);
        var fids = query.Fids?.ToArray();
        return NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBQueryVectorEx),
            (out IntPtr output) => NativeDdb.DDBQueryVectorEx(vectorPath, query.LayerName, query.Bbox, query.BboxSrs,
                fids, fids?.Length ?? 0, query.Where, query.MaxFeatures, query.StartIndex,
                query.OutputFormat ?? "application/json", out output));
    }

    public long CountVector(string vectorPath, VectorQuery query)
    {
        ValidateVectorQuery(vectorPath, query);
        var fids = query.Fids?.ToArray();
        var count = 0L;
        NativeCall.Invoke(nameof(NativeDdb.DDBCountVector),
            () => NativeDdb.DDBCountVector(vectorPath, query.LayerName, query.Bbox, query.BboxSrs, fids,
                fids?.Length ?? 0, query.Where, out count));
        return count;
    }

    public string DescribeVector(string vectorPath, string? layerName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vectorPath);
        return NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBDescribeVector),
            (out IntPtr output) => NativeDdb.DDBDescribeVector(vectorPath, layerName, out output));
    }

    public string MakeCadPrj(string crs)
    {
        ArgumentNullException.ThrowIfNull(crs);
        try
        {
            return NativeCall.InvokeRequiredString(nameof(NativeDdb.DDBMakeCadPrj),
                (out IntPtr output) => NativeDdb.DDBMakeCadPrj(crs, out output));
        }
        catch (DdbException ex) when (ex is DdbInvalidArgumentsException || ex.GetType() == typeof(DdbException))
        {
            // The port's contract: a value that is not a coordinate system is an argument error
            throw new ArgumentException(ex.Message, nameof(crs), ex);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static string NormalizeSeparators(string path) => path.Replace('\\', '/');

    private static string[] NormalizeSeparators(string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Array.ConvertAll(paths, p => p == null ? null! : NormalizeSeparators(p));
    }

    // A missing file is a clear managed error, not a native one
    private static void RequireFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new DdbException($"File not found: '{filePath}'. Cannot generate thumbnail for non-existent file.");
    }

    private static void RequirePaths(string[] paths)
    {
        if (paths == null || paths.Length == 0)
            throw new ArgumentException("paths is null or empty", nameof(paths));
    }

    private static void RequireBbox(double[] bbox)
    {
        if (bbox == null || bbox.Length != 4)
            throw new ArgumentException("bbox must contain exactly 4 elements: [minX,minY,maxX,maxY]", nameof(bbox));
    }

    private static void RequireSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("width and height must be positive");
    }

    private static void ValidateVectorQuery(string vectorPath, VectorQuery query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vectorPath);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Bbox != null && query.Bbox.Length != 4)
            throw new ArgumentException("bbox must contain exactly 4 elements when provided", nameof(query));
    }
}
