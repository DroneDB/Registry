namespace Registry.Ports.DroneDB;

/// <summary>
/// A file of a build as libddb resolves it (<c>DDBResolveBuildFile</c>): where it is, for a declared artifact the
/// media type its manifest gives, and the current version of the artifact it belongs to.
/// </summary>
/// <param name="Path">The absolute path of the file.</param>
/// <param name="MediaType">
/// The manifest's media type of the artifact, or null when libddb does not give one (a file below a directory
/// artifact, or a libddb that resolves the path only): the caller then falls back to the media type of the extension.
/// </param>
/// <param name="Version">
/// The <see cref="BuildArtifact.Version"/> of the artifact the file belongs to as libddb lists it now (the tiles of a
/// directory have the version of its index), or null from a libddb without it.
/// </param>
public sealed record ResolvedBuildFile(string Path, string? MediaType = null, string? Version = null);
