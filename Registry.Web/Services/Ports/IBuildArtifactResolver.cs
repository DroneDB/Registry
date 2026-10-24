#nullable enable
using System.Collections.Generic;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Ports;

/// <summary>
/// The builds of dataset entries by role (plan decision D8, XI-04): libddb's build manifest
/// (<see cref="IDdbBuild.GetBuildArtifacts"/>) says whether an entry is buildable, what state its build
/// is in and which artifacts exist, each with a role (<see cref="ArtifactRoles"/>), a path relative to
/// the build root, a media type and an optional download name. Registry chooses roles here and serves
/// the paths libddb returns; it never names a build folder, an artifact file or a buildable type.
/// </summary>
public interface IBuildArtifactResolver
{
    /// <summary>
    /// Whether libddb builds entries of <paramref name="type"/> at all (<c>DDBGetEntryTypes</c>, read once per
    /// process): a listing asks the manifest only about the entries that may have a build.
    /// </summary>
    bool IsBuildableType(EntryType type);

    /// <summary>
    /// Whether entries of <paramref name="type"/> have a thumbnail at all (<c>DDBGetEntryTypes</c>: "file" or "build",
    /// not "none"): the one rule of the thumbnail endpoints, read once per process. A type without one (models,
    /// videos, panoramas, vectors, ...) has nothing a renderer could open.
    /// </summary>
    bool HasThumbnail(EntryType type);

    /// <summary>The build of one entry (one native call).</summary>
    EntryBuildArtifacts GetBuild(IDDB ddb, string entryPath);

    /// <summary>The builds of several entries with one native call, one element per path, in input order.</summary>
    IReadOnlyList<EntryBuildArtifacts> GetBuilds(IDDB ddb, IReadOnlyList<string> entryPaths);

    /// <summary>
    /// The dataset path of the file a renderer (thumbnails, tiles, raster analysis) reads for
    /// <paramref name="entry"/>: the artifact of its own build that a renderer reads, or the entry itself
    /// when it is not built for rendering (not buildable, a sidecar, a type with no rendered role).
    /// </summary>
    /// <returns>The path, or null when the entry is rendered from an artifact that does not exist yet
    /// (build pending, running or failed).</returns>
    string? GetRenderSource(IDDB ddb, Entry entry);

    /// <summary>
    /// The artifact of <paramref name="build"/> offered as a download (the first one with a download
    /// name), with the local file, the name and the media type it is served with.
    /// </summary>
    /// <returns>The download, or null when the entry's own build has no downloadable artifact.</returns>
    BuildDownload? GetDownload(EntryBuildArtifacts build);

    /// <summary>
    /// The artifact with <paramref name="role"/> of the build named by <paramref name="hash"/>, when it exists: what
    /// the OGC services and the MVT tiles read. The entry is <paramref name="entryPath"/> when the caller knows it,
    /// else the entry with that hash among the types whose builds produce the role (libddb's entry types). A request
    /// pays the manifest once per build: the location is kept (process memory, bounded) while it exists, and a
    /// role's location in a build does not change within a libddb version.
    /// </summary>
    /// <param name="ddb">The dataset.</param>
    /// <param name="hash">The hash that names the build.</param>
    /// <param name="role">The artifact's role (<see cref="ArtifactRoles"/>).</param>
    /// <param name="relativePath">A file inside a directory artifact ('/'-separated, e.g. an MVT tile); null for the
    /// artifact's own file: a file artifact itself, a directory artifact's descriptor.</param>
    /// <param name="entryPath">The entry whose build it is, when the caller knows it.</param>
    /// <returns>The local path of the file when it exists, else null.</returns>
    string? FindArtifactFile(IDDB ddb, string hash, string role, string? relativePath = null, string? entryPath = null);

    /// <summary>
    /// The absolute path of a file of a build (an artifact or a file below a directory artifact),
    /// containment-checked by libddb; null when the file is not one of the build's files.
    /// </summary>
    /// <exception cref="DdbInvalidArgumentsException">The hash is malformed or the path breaks the path rules.</exception>
    string? ResolveFile(IDDB ddb, string hash, string path);

    /// <summary>
    /// Copies the builds of <paramref name="entryPaths"/> (artifacts and pending markers) from
    /// <paramref name="source"/> to <paramref name="destination"/>, once per build: what a transfer
    /// needs so the transferred files are not built again. Entries without a build of their own (not
    /// buildable, sidecars) or with nothing built yet are skipped.
    /// </summary>
    /// <returns>The number of builds copied.</returns>
    int CopyBuilds(IDDB source, IReadOnlyList<string> entryPaths, IDDB destination);
}

/// <summary>A build artifact served as a download: its local file, the file name offered and the media type sent.</summary>
public sealed record BuildDownload(string LocalPath, string FileName, string MediaType);
