using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// Cache identity of a WMS GetMap render.
/// <para>
/// The BBOX is snapped to the pixel grid of the request: the pixel size is kept to 6 significant
/// digits and the lower-left corner is moved to the nearest multiple of it. Requests whose corners
/// differ by less than half a pixel (float noise, client rounding, a pan back to a previous view)
/// therefore share one render, and a client panning by whole pixels stays on the same grid. The
/// server always renders the snapped BBOX, so a cached image is exactly what a fresh render would
/// return; it is offset from the requested BBOX by at most half a pixel.
/// </para>
/// </summary>
public sealed class WmsRenderKey
{
    /// <summary>BBOX to render (minX, minY, maxX, maxY in the request CRS, x/east first).</summary>
    public double[] Bbox { get; }

    /// <summary>Cache key of the render: grid cell, size, CRS, format, background and layers.</summary>
    public string Key { get; }

    private WmsRenderKey(double[] bbox, string key)
    {
        Bbox = bbox;
        Key = key;
    }

    /// <param name="bbox">Requested BBOX, x/east first (as parsed by OgcRequestParser).</param>
    /// <param name="layers">Identity of each layer in drawing order: its content hash and style.</param>
    public static WmsRenderKey Build(double[] bbox, string crs, int width, int height, string format,
        bool transparent, string? bgColor, IEnumerable<(string Hash, string Style)> layers)
    {
        ArgumentNullException.ThrowIfNull(bbox);
        if (bbox.Length < 4 || width <= 0 || height <= 0)
            throw new ArgumentException("Invalid BBOX or size");

        var resX = SignificantDigits((bbox[2] - bbox[0]) / width, 6);
        var resY = SignificantDigits((bbox[3] - bbox[1]) / height, 6);
        var col = Math.Round(bbox[0] / resX);
        var row = Math.Round(bbox[1] / resY);

        var snapped = new[] { col * resX, row * resY, col * resX + width * resX, row * resY + height * resY };

        var layerKey = string.Join(",", layers.Select(l => $"{l.Hash}~{(l.Style ?? string.Empty).ToUpperInvariant()}"));
        var key = string.Join("|",
            crs.ToUpperInvariant(),
            Format(resX), Format(resY), Format(col), Format(row),
            $"{width}x{height}",
            format.ToLowerInvariant(),
            transparent ? "t" : "o",
            (bgColor ?? string.Empty).ToUpperInvariant(),
            layerKey);

        return new WmsRenderKey(snapped, key);
    }

    private static double SignificantDigits(double value, int digits)
    {
        if (value == 0 || double.IsNaN(value) || double.IsInfinity(value))
            return value;
        var scale = Math.Pow(10, digits - 1 - (int)Math.Floor(Math.Log10(Math.Abs(value))));
        return Math.Round(value * scale) / scale;
    }

    private static string Format(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
