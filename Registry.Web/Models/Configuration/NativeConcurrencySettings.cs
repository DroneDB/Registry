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

    public int TilesOrDefault => Tiles > 0 ? Tiles : Math.Max(2, Environment.ProcessorCount / 2);
    public int HeavyOrDefault => Heavy > 0 ? Heavy : Math.Max(1, Environment.ProcessorCount / 4);
}
