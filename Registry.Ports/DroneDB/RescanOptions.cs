namespace Registry.Ports.DroneDB;

/// <summary>
/// Selects what an index rescan re-reads. An entry must match one of <see cref="Types"/>
/// (when set) and one of <see cref="Extensions"/> (when set).
/// </summary>
/// <param name="Types">Comma-separated entry types (e.g. "image,geoimage"), or null/empty for all.</param>
/// <param name="Extensions">Comma-separated file extensions (e.g. "dwg,dxf"), or null/empty for all.
/// Needs a DroneDB library that exports DDBRescanEx.</param>
/// <param name="StopOnError">Whether to stop at the first entry error.</param>
public sealed record RescanOptions(string? Types = null, string? Extensions = null, bool StopOnError = true);
