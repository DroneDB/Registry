using System;
using System.Globalization;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;

namespace Registry.Web.Services.Objects;

/// <summary>
/// The rules of the options of the raster endpoints that are not libddb's: a NoData value and a WGS 84 window are
/// finite numbers, and a window is four of them in the order <c>minLon,minLat,maxLon,maxLat</c>. A refusal is a 400.
/// </summary>
public static class RasterOptionRules
{
    /// <summary>A NoData value that replaces the one of the bands must be a finite number.</summary>
    public static void RequireNodata(double? nodata)
    {
        if (nodata is { } value && !double.IsFinite(value))
            throw new BadRequestException("'nodata' must be a finite number");
    }

    /// <summary>
    /// The WGS 84 window of the text <c>minLon,minLat,maxLon,maxLat</c> (invariant culture); null when there is no text.
    /// </summary>
    public static double[]? ParseBbox(string? bbox)
    {
        if (string.IsNullOrWhiteSpace(bbox)) return null;

        var parts = bbox.Split(',', StringSplitOptions.TrimEntries);
        var values = new double[4];
        if (parts.Length != 4)
            throw new BadRequestException("'bbox' must be minLon,minLat,maxLon,maxLat (WGS 84)");
        for (var i = 0; i < 4; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) ||
                !double.IsFinite(values[i]))
                throw new BadRequestException("'bbox' must be four finite numbers: minLon,minLat,maxLon,maxLat (WGS 84)");

        RequireWindow(values);
        return values;
    }

    /// <summary>A window must be inside the longitude and latitude of WGS 84.</summary>
    public static void RequireWindow(double[] bbox)
    {
        if (bbox[0] < -180 || bbox[2] > 180 || bbox[1] < -90 || bbox[3] > 90)
            throw new BadRequestException("'bbox' must be in WGS 84 degrees: longitude -180..180, latitude -90..90");
    }

    /// <summary>The options of the values with the checks of the endpoint applied.</summary>
    public static RasterAnalysisOptions Options(double? nodata, string? formula = null, double[]? bbox = null)
    {
        RequireNodata(nodata);
        return new RasterAnalysisOptions(nodata, string.IsNullOrWhiteSpace(formula) ? null : formula.Trim(), bbox);
    }
}
