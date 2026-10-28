#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Registry.Web.Exceptions;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// WMS 1.3.0 / 1.1.1 request validation. All checks throw
/// <see cref="OgcException"/> with the appropriate OGC exception code so
/// <see cref="OgcExceptionFilter"/> can serialize them to ServiceExceptionReport.
///
/// Centralizing these checks here lets the controller fail fast (before BBOX parsing)
/// and lets the manager assert post-conditions defensively. The same whitelist is
/// surfaced by GetCapabilities so the advertised contract matches enforcement (the styles of a
/// raster come from libddb, see <see cref="ResolveIndexStyle"/>).
/// </summary>
public static class WmsValidator
{
    public static readonly string[] SupportedCrs = ["EPSG:4326", "EPSG:3857", "CRS:84"];
    public static readonly string[] SupportedMapFormats = ["image/png", "image/jpeg", "image/webp"];
    public static readonly string[] SupportedInfoFormats =
        ["application/json", "text/xml", "application/xml", "text/html", "text/plain"];

    public const int MinDim = 1;
    public const int MaxDim = 4096;

    public static void ValidateCrs(string? crs)
    {
        if (string.IsNullOrWhiteSpace(crs))
            throw new OgcException("MissingParameterValue", "CRS/SRS is required", 400, "CRS");
        if (!SupportedCrs.Any(c => string.Equals(c, crs, StringComparison.OrdinalIgnoreCase)))
            throw new OgcException("InvalidCRS", $"CRS '{crs}' is not supported", 400, "CRS");
    }

    public static void ValidateMapFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            throw new OgcException("MissingParameterValue", "FORMAT is required", 400, "FORMAT");
        if (!SupportedMapFormats.Any(f => string.Equals(f, format, StringComparison.OrdinalIgnoreCase)))
            throw new OgcException("InvalidFormat", $"FORMAT '{format}' is not supported", 400, "FORMAT");
    }

    public static void ValidateInfoFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            throw new OgcException("MissingParameterValue", "INFO_FORMAT is required", 400, "INFO_FORMAT");
        if (!SupportedInfoFormats.Any(f => string.Equals(f, format, StringComparison.OrdinalIgnoreCase)))
            throw new OgcException("InvalidFormat", $"INFO_FORMAT '{format}' is not supported", 400, "INFO_FORMAT");
    }

    public static void ValidateDimensions(int width, int height)
    {
        if (width < MinDim || width > MaxDim)
            throw new OgcException("InvalidParameterValue",
                $"WIDTH must be in [{MinDim},{MaxDim}]", 400, "WIDTH");
        if (height < MinDim || height > MaxDim)
            throw new OgcException("InvalidParameterValue",
                $"HEIGHT must be in [{MinDim},{MaxDim}]", 400, "HEIGHT");
    }

    public static void ValidateLayers(string[]? layers)
    {
        if (layers == null || layers.Length == 0)
            throw new OgcException("MissingParameterValue", "LAYERS is required", 400, "LAYERS");
    }

    /// <summary>The natural-colour style: an empty STYLES entry or <c>default</c>.</summary>
    public static bool IsDefaultStyle(string? style) =>
        string.IsNullOrEmpty(style) || string.Equals(style, "default", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A non-default STYLES entry of a raster layer: one of the spectral indices libddb lists for that raster
    /// (<paramref name="supportedIndexes"/>, <see cref="Registry.Ports.DroneDB.RasterIndexLayer"/>), the same
    /// list GetCapabilities advertises. libddb owns which indices a raster can render; nothing here knows their names.
    /// </summary>
    /// <returns>The index as libddb names it (the case of the request does not matter).</returns>
    /// <exception cref="OgcException">StyleNotDefined: the raster has no such style.</exception>
    public static string ResolveIndexStyle(string style, string layerName, IReadOnlyCollection<string> supportedIndexes)
    {
        var index = supportedIndexes.FirstOrDefault(i => string.Equals(i, style, StringComparison.OrdinalIgnoreCase));
        return index ?? throw new OgcException("StyleNotDefined",
            $"Style '{style}' is not defined for layer '{layerName}'", 400, "STYLES");
    }
}
