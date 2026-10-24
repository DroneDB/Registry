#nullable enable
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Caching.Memory;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Default <see cref="IBuildArtifactResolver"/>, over libddb's build manifest (XI-04): the one place where
/// Registry chooses which artifact role a renderer reads and which media type a download is served
/// with. The entry types come from libddb once per process; the locations of existing artifacts are kept in a
/// bounded process-memory cache (<see cref="FindArtifact"/>).
/// </summary>
public sealed class BuildArtifactResolver : IBuildArtifactResolver, IDisposable
{
    /// <summary>Locations of artifacts kept: enough for every built layer a host serves at once.</summary>
    private const int LocationCacheSize = 20_000;

    private static readonly TimeSpan LocationExpiry = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The roles the renderers read (thumbnails, tiles, raster analysis), in order of preference: the
    /// first existing one of an entry's own build is its render source. libddb lists the legacy EPT
    /// output only when there is no COPC, so the order never picks EPT over COPC.
    /// </summary>
    private static readonly string[] RenderRoles =
    [
        ArtifactRoles.RasterCog, ArtifactRoles.PointCloudCopc, ArtifactRoles.PointCloudEpt,
        ArtifactRoles.VectorGpkg, ArtifactRoles.ModelNexus, ArtifactRoles.GsplatLod
    ];

    private readonly Lazy<FrozenSet<EntryType>> _renderedTypes;
    private readonly Lazy<FrozenSet<EntryType>> _buildableTypes;
    private readonly Lazy<FrozenSet<EntryType>> _thumbnailTypes;
    private readonly Lazy<IReadOnlyList<EntryTypeInfo>> _types;
    private readonly IFileSystem _fs;
    private readonly MemoryCache _locations = new(new MemoryCacheOptions { SizeLimit = LocationCacheSize });

    public BuildArtifactResolver(IDdbWrapper ddbWrapper, IFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(ddbWrapper);
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _types = new Lazy<IReadOnlyList<EntryTypeInfo>>(ddbWrapper.GetEntryTypes);

        // libddb's type rules: the types it builds, and those whose builds can produce a role a renderer reads
        _buildableTypes = new Lazy<FrozenSet<EntryType>>(() => _types.Value
            .Where(t => t.Buildable)
            .Select(t => t.Id)
            .ToFrozenSet());
        _thumbnailTypes = new Lazy<FrozenSet<EntryType>>(() => _types.Value
            .Where(t => t.HasThumbnail)
            .Select(t => t.Id)
            .ToFrozenSet());
        _renderedTypes = new Lazy<FrozenSet<EntryType>>(() => _types.Value
            .Where(t => t.Roles.Any(r => RenderRoles.Contains(r, StringComparer.Ordinal)))
            .Select(t => t.Id)
            .ToFrozenSet());
    }

    public IReadOnlyList<EntryTypeInfo> EntryTypes => _types.Value;

    public bool IsBuildableType(EntryType type) => _buildableTypes.Value.Contains(type);

    public bool HasThumbnail(EntryType type) => _thumbnailTypes.Value.Contains(type);

    public EntryBuildArtifacts GetBuild(IDDB ddb, string entryPath) => GetBuilds(ddb, [entryPath])[0];

    public IReadOnlyList<EntryBuildArtifacts> GetBuilds(IDDB ddb, IReadOnlyList<string> entryPaths)
    {
        ArgumentNullException.ThrowIfNull(ddb);
        ArgumentNullException.ThrowIfNull(entryPaths);

        var builds = ddb.GetBuildArtifacts(entryPaths);
        if (builds.Count != entryPaths.Count)
            throw new DdbException(
                $"The build manifest has {builds.Count} element(s) for {entryPaths.Count} path(s)");
        return builds;
    }

    public string? GetRenderSource(IDDB ddb, Entry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // A type that never renders from a build costs no native call (images: the hot thumbnail path)
        if (!_renderedTypes.Value.Contains(entry.Type))
            return entry.Path;

        var build = GetBuild(ddb, entry.Path);
        if (!build.IsOwnBuild())
            return entry.Path;

        var artifact = RenderRoles
            .Select(role => build.Build!.Find(role))
            .FirstOrDefault(a => a != null);
        if (artifact == null)
            return null;

        // A directory artifact (legacy EPT) is read through its descriptor
        var local = build.Build!.LocalPathOf(artifact);
        if (artifact.Index is { Length: > 0 } index)
            local = Path.Combine(local, index.ToLocalSeparators());

        return Path.GetRelativePath(Path.GetFullPath(ddb.DatasetFolderPath), local).ToDdbPath();
    }

    public BuildDownload? GetDownload(EntryBuildArtifacts build)
    {
        ArgumentNullException.ThrowIfNull(build);

        if (!build.IsOwnBuild())
            return null;

        var artifact = build.Build!.Artifacts.FirstOrDefault(a => !string.IsNullOrEmpty(a.DownloadName));
        if (artifact == null)
            return null;

        return new BuildDownload(build.Build.LocalPathOf(artifact), artifact.DownloadName!, artifact.MediaType);
    }

    public string? FindArtifactFile(IDDB ddb, string hash, string role, string? relativePath = null,
        string? entryPath = null)
    {
        if (FindArtifact(ddb, hash, role, entryPath) is not { } located)
            return null;

        var file = relativePath != null ? located.Under(relativePath)
            : located.Artifact.Index is { Length: > 0 } index ? located.Under(index)
            : located.LocalPath;
        return _fs.Exists(file) ? file : null;
    }

    private LocatedArtifact? FindArtifact(IDDB ddb, string hash, string role, string? entryPath)
    {
        ArgumentNullException.ThrowIfNull(ddb);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        var key = (Path.GetFullPath(ddb.DatasetFolderPath), hash, role);
        if (_locations.TryGetValue(key, out LocatedArtifact? known) && known != null)
        {
            if (Exists(known))
                return known;
            _locations.Remove(key);
        }

        var located = Locate(ddb, hash, role, entryPath);
        if (located != null)
            _locations.Set(key, located, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = LocationExpiry });
        return located;
    }

    // The entry of the build (given, or found by hash among the types whose builds produce the role), then the
    // manifest of its build
    private LocatedArtifact? Locate(IDDB ddb, string hash, string role, string? entryPath)
    {
        string[] paths;
        if (entryPath != null)
            paths = [entryPath];
        else
        {
            var types = _types.Value.Where(t => t.Roles.Contains(role, StringComparer.Ordinal)).Select(t => t.Id).ToArray();
            if (types.Length == 0)
                return null;
            paths = [.. ddb.FindSummaries(EntryQuery.OfTypes(null, types))
                .Where(e => string.Equals(e.Hash, hash, StringComparison.Ordinal))
                .Select(e => e.Path)];
        }

        if (paths.Length == 0)
            return null;

        return GetBuilds(ddb, paths)
            .Where(b => b.IsOwnBuild() && string.Equals(b.Build!.Hash, hash, StringComparison.Ordinal))
            .Select(b => (Build: b.Build!, Artifact: b.Build!.Find(role)))
            .Where(f => f.Artifact != null)
            .Select(f => new LocatedArtifact(f.Build.LocalPathOf(f.Artifact!), f.Artifact!))
            .FirstOrDefault();
    }

    private bool Exists(LocatedArtifact located) =>
        located.Artifact.Kind == "dir" ? _fs.FolderExists(located.LocalPath) : _fs.Exists(located.LocalPath);

    public void Dispose() => _locations.Dispose();

    // An existing artifact of a build and its local path (a file, or a directory artifact's directory)
    private sealed record LocatedArtifact(string LocalPath, BuildArtifact Artifact)
    {
        public string Under(string relativePath) => Path.Combine(LocalPath, relativePath.ToLocalSeparators());
    }

    public string? ResolveFile(IDDB ddb, string hash, string path)
    {
        ArgumentNullException.ThrowIfNull(ddb);
        return ddb.ResolveBuildFile(hash, path);
    }

    public int CopyBuilds(IDDB source, IReadOnlyList<string> entryPaths, IDDB destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (entryPaths.Count == 0)
            return 0;

        var hashes = GetBuilds(source, entryPaths)
            .Where(b => b.IsOwnBuild() && (b.Build!.Artifacts.Count > 0 || b.Build.State == BuildState.Pending))
            .Select(b => b.Build!.Hash!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var hash in hashes)
            source.CopyBuildArtifactsTo(destination.DatasetFolderPath, hash);

        return hashes.Length;
    }
}

/// <summary>Reading libddb's build manifest (XI-04).</summary>
public static class BuildManifestExtensions
{
    /// <summary>The entry is buildable (by itself, or as a sidecar of its main file).</summary>
    public static bool CanBeBuilt(this EntryBuildArtifacts build) => build.Build?.Buildable == true;

    /// <summary>
    /// The build is the entry's own: it is buildable and not a sidecar (whose build, and artifacts, are its
    /// main file's).
    /// </summary>
    public static bool IsOwnBuild(this EntryBuildArtifacts build) =>
        build.Build is { Buildable: true, Hash: not null, Root: not null } own &&
        string.Equals(own.Source, build.Path, StringComparison.Ordinal);

    /// <summary>A live build holds the entry's build lock.</summary>
    public static bool HasLiveBuild(this EntryBuildArtifacts build) => build.Build?.State == BuildState.Building;

    /// <summary>The existing artifact with <paramref name="role"/>, or null.</summary>
    public static BuildArtifact? Find(this EntryBuild build, string role) =>
        build.Artifacts.FirstOrDefault(a => string.Equals(a.Role, role, StringComparison.Ordinal));

    /// <summary>The absolute local path of <paramref name="artifact"/> (relative to the build root).</summary>
    public static string LocalPathOf(this EntryBuild build, BuildArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(build.Root);
        return Path.GetFullPath(Path.Combine(build.Root, artifact.Path.ToLocalSeparators()));
    }
}
