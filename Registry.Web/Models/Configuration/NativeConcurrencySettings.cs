#nullable enable
using System;

namespace Registry.Web.Models.Configuration;

/// <summary>
/// How many native renders run at once in this process (REG-07 §5.2). Bound from
/// <c>AppSettings:NativeConcurrency</c>; a value of 0 or less means the default.
/// </summary>
public class NativeConcurrencySettings
{
    /// <summary>Map tiles and map renders (WMTS/WMS/OGC API tiles). Default: max(2, cores / 2).</summary>
    public int Tiles { get; set; }

    /// <summary>Thumbnails and coverage renders. Default: max(1, cores / 4).</summary>
    public int Heavy { get; set; }

    public int TilesOrDefault => TilesFor(Environment.ProcessorCount);
    public int HeavyOrDefault => HeavyFor(Environment.ProcessorCount);

    /// <summary>The tiles lane width for a process that may use <paramref name="cores"/> cores.</summary>
    public int TilesFor(int cores) => Tiles > 0 ? Tiles : Math.Max(2, cores / 2);

    /// <summary>The heavy lane width for a process that may use <paramref name="cores"/> cores.</summary>
    public int HeavyFor(int cores) => Heavy > 0 ? Heavy : Math.Max(1, cores / 4);
}
