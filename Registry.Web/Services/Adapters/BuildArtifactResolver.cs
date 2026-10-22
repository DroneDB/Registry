#nullable enable
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Default <see cref="IBuildArtifactResolver"/>, over libddb's build manifest (XI-04): the one place where
/// Registry chooses which artifact role a renderer reads and which media type a download is served
/// with. Stateless; the entry types come from libddb once per process.
/// </summary>
public sealed class BuildArtifactResolver : IBuildArtifactResolver
{
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

    /// <summary>
    /// The media types Registry has always served the downloadable artifacts with, by role. The manifest
    /// gives the registered types (<c>image/tiff; application=geotiff; profile=cloud-optimized</c>,
    /// <c>application/vnd.laszip+copc</c>, <c>application/geopackage+sqlite3</c>); switching to them is
    /// a decision the user has not taken yet (RQ-11 RESULTS, "Media types"). When it is taken, delete
    /// this table: <see cref="GetDownload"/> and the entries' artifact links then serve <see cref="BuildArtifact.MediaType"/>.
    /// A role without an entry is served with the manifest's type.
    /// </summary>
    private static readonly FrozenDictionary<string, string> ServedMediaTypes = new Dictionary<string, string>
    {
        [ArtifactRoles.RasterCog] = "image/tiff",
        [ArtifactRoles.PointCloudCopc] = "application/octet-stream",
        [ArtifactRoles.VectorGpkg] = "application/geopackage+sqlite"
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly Lazy<FrozenSet<EntryType>> _renderedTypes;
    private readonly Lazy<FrozenSet<EntryType>> _buildableTypes;

    public BuildArtifactResolver(IDdbWrapper ddbWrapper)
    {
        ArgumentNullException.ThrowIfNull(ddbWrapper);

        // libddb's type rules: the types it builds, and those whose builds can produce a role a renderer reads
        _buildableTypes = new Lazy<FrozenSet<EntryType>>(() => ddbWrapper.GetEntryTypes()
            .Where(t => t.Buildable)
            .Select(t => t.Id)
            .ToFrozenSet());
        _renderedTypes = new Lazy<FrozenSet<EntryType>>(() => ddbWrapper.GetEntryTypes()
            .Where(t => t.Roles.Any(r => RenderRoles.Contains(r, StringComparer.Ordinal)))
            .Select(t => t.Id)
            .ToFrozenSet());
    }

    public bool IsBuildableType(EntryType type) => _buildableTypes.Value.Contains(type);

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

        return new BuildDownload(build.Build.LocalPathOf(artifact), artifact.DownloadName!, ServedMediaType(artifact));
    }

    public string ServedMediaType(BuildArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return ServedMediaTypes.GetValueOrDefault(artifact.Role, artifact.MediaType);
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
