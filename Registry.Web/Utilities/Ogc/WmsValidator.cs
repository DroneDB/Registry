using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// WMS 1.3.0 / 1.1.1 request validation. All checks throw
/// <see cref="OgcException"/> with the appropriate OGC exception code so
/// <see cref="OgcExceptionFilter"/> can serialize them to ServiceExceptionReport.
///
/// Centralizing these checks here lets the controller fail fast (before BBOX parsing)
/// and lets the manager assert post-conditions defensively. The same whitelist is
/// surfaced by GetCapabilities so the advertised contract matches enforcement.
/// </summary>
public static class WmsValidator
{
    public static readonly string[] SupportedCrs = { "EPSG:4326", "EPSG:3857", "CRS:84" };
    public static readonly string[] SupportedMapFormats = { "image/png", "image/jpeg", "image/webp" };
    public static readonly string[] SupportedInfoFormats =
        { "application/json", "text/xml", "application/xml", "text/html", "text/plain" };
    public static readonly string[] SpectralIndexes = { "NDVI", "NDRE", "NDWI", "EVI", "SAVI" };

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

    /// <summary>
    /// STYLES validation. Empty entries and "default" are always allowed.
    /// Spectral index styles (NDVI/NDRE/NDWI/EVI/SAVI) are only allowed when
    /// the corresponding layer is multispectral.
    /// </summary>
    public static void ValidateStyles(string[]? styles, string[] layers,
        Func<string, OgcLayerDto?> layerResolver)
    {
        if (styles == null) return;
        for (var i = 0; i < styles.Length; i++)
        {
            var s = styles[i];
            if (string.IsNullOrEmpty(s)) continue;
            if (string.Equals(s, "default", StringComparison.OrdinalIgnoreCase)) continue;

            if (IsSpectralIndex(s))
            {
                var layerName = i < layers.Length ? layers[i] : null;
                var layer = string.IsNullOrEmpty(layerName) ? null : layerResolver(layerName);
                if (layer == null || layer.EntryType != Registry.Ports.DroneDB.EntryType.GeoRaster
                    || !layer.IsMultispectral)
                {
                    throw new OgcException("StyleNotDefined",
                        $"Spectral style '{s}' is only defined for multispectral raster layers", 400, "STYLES");
                }
                continue;
            }
            throw new OgcException("StyleNotDefined", $"Style '{s}' is not defined", 400, "STYLES");
        }
    }

    public static bool IsSpectralIndex(string? style)
    {
        if (string.IsNullOrWhiteSpace(style)) return false;
        var s = style.Trim().ToUpperInvariant();
        return Array.IndexOf(SpectralIndexes, s) >= 0;
    }
}
