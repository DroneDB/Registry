namespace Registry.Ports.DroneDB;

/// <summary>
/// A file of a build as libddb resolves it (<c>DDBResolveBuildFile</c>): where it is and, for a declared artifact,
/// the media type its manifest gives.
/// </summary>
/// <param name="Path">The absolute path of the file.</param>
/// <param name="MediaType">
/// The manifest's media type of the artifact, or null when libddb does not give one (a file below a directory
/// artifact, or a libddb that resolves the path only): the caller then falls back to the media type of the extension.
/// </param>
public sealed record ResolvedBuildFile(string Path, string? MediaType = null);
