using System.Collections.Generic;

namespace Registry.Ports.DroneDB;

/// <summary>
/// Selects what an index rescan re-reads. An entry must be one of <see cref="Paths"/> or under one of them
/// (when set), match one of <see cref="Types"/> (when set) and one of <see cref="Extensions"/> (when set).
/// </summary>
/// <param name="Types">Comma-separated entry types (e.g. "image,geoimage"), or null/empty for all.</param>
/// <param name="Extensions">Comma-separated file extensions (e.g. "dwg,dxf"), or null/empty for all.</param>
/// <param name="StopOnError">Whether to stop at the first entry error.</param>
/// <param name="Paths">Dataset-relative (normalized) entry paths: a file selects itself, a folder the entries under it.
/// A path that is not in the index is an error item of the result. Null or empty rescans every entry. Each entry is
/// committed on its own, so the rest of the dataset stays writable while the rescan runs.</param>
public sealed record RescanOptions(string? Types = null, string? Extensions = null, bool StopOnError = true,
    IReadOnlyList<string>? Paths = null);
