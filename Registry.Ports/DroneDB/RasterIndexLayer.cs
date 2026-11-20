using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Registry.Ports.Json;

namespace Registry.Ports.DroneDB;

/// <summary>
/// The <c>indexLayer</c> member of <see cref="IDdbRaster.GetRasterInfo"/> (ddb.h <c>DDBGetRasterInfo</c>): the
/// spectral indices libddb can render from a raster (<c>DDBRenderRasterIndex</c>), upper-case, in its order,
/// empty when none. libddb owns the rule (band order from the sensor profile, the fixed order for five unnamed
/// data bands or more, SAVI and EVI only over floating-point rasters): Registry reads the list and never
/// re-derives it from the bands.
/// </summary>
public static class RasterIndexLayer
{
    /// <summary>The member of the raster info document that holds the list.</summary>
    public const string Member = "indexLayer";

    /// <summary>
    /// The list of a raster info document, or null when the document has no such array (libddb always writes it,
    /// so null is a contract violation: the caller must not take it for "no index").
    /// </summary>
    /// <exception cref="JsonException">The text is not JSON.</exception>
    public static IReadOnlyList<string>? Parse(string rasterInfoJson)
    {
        ArgumentNullException.ThrowIfNull(rasterInfoJson);
        using var document = JsonDocument.Parse(rasterInfoJson);
        if (document.RootElement.Member(Member) is not { ValueKind: JsonValueKind.Array } list)
            return null;

        return [.. list.Items().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!)];
    }
}
