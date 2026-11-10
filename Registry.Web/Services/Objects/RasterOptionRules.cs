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

    /// <summary>The first libddb ABI minor that reads the unit of the rescale (<c>DDBRasterOptions.rescaleUnit</c>, D-022).</summary>
    public const int RescaleUnitAbiMinor = 3;

    /// <summary>
    /// The unit the rescale of a thermal formula is written in (<c>rescaleUnit</c> of a tile, a thumbnail or an export),
    /// trimmed, or null when there is none. libddb converts the rescale to the unit of the formula and refuses a unit it
    /// does not know (<see cref="IsKnownTemperatureUnit"/>), which is a 400 at <c>rescaleUnit</c> before any native work;
    /// a libddb before 2.3 has no such option and would refuse the struct that sets it, so there it is a 400 at
    /// <c>rescaleUnit</c> that says why.
    /// </summary>
    /// <exception cref="InvalidFieldException">A unit libddb does not know, or a unit on a libddb before 2.3.</exception>
    public static string? RescaleUnit(string? rescaleUnit, DdbCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (string.IsNullOrWhiteSpace(rescaleUnit))
            return null;

        if (!IsKnownTemperatureUnit(rescaleUnit))
            throw new InvalidFieldException("rescaleUnit",
                $"'{rescaleUnit.Trim()}' is not a temperature unit: use K (Kelvin), C (Celsius) or F (Fahrenheit).");

        if (capabilities.AbiMinor < RescaleUnitAbiMinor)
            throw new InvalidFieldException("rescaleUnit",
                $"A rescale in another temperature unit needs libddb 2.{RescaleUnitAbiMinor} or later: this server runs libddb {capabilities.Version} (ABI {capabilities.AbiMajor}.{capabilities.AbiMinor}). Send the rescale in the unit of the formula.");

        return rescaleUnit.Trim();
    }

    /// <summary>
    /// Whether libddb reads <paramref name="unit"/> as a temperature unit (<c>parseTemperatureUnit</c> of libddb): only its
    /// ASCII letters and digits count, in any case, so <c>K</c>, <c>Kelvin</c>, <c>degK</c>; <c>C</c>, <c>Celsius</c>,
    /// <c>degC</c>, <c>°C</c>, <c>Centigrade</c>; <c>F</c>, <c>Fahrenheit</c>, <c>degF</c>, <c>°F</c> (and <c>deg C</c>).
    /// </summary>
    public static bool IsKnownTemperatureUnit(string? unit)
    {
        if (unit is null) return false;

        var letters = new System.Text.StringBuilder(unit.Length);
        foreach (var c in unit)
            if (char.IsAsciiLetterOrDigit(c))
                letters.Append(char.ToLowerInvariant(c));

        return letters.ToString() is "k" or "kelvin" or "degk"
            or "c" or "celsius" or "degc" or "centigrade"
            or "f" or "fahrenheit" or "degf";
    }

    /// <summary>The options of the values with the checks of the endpoint applied.</summary>
    public static RasterAnalysisOptions Options(double? nodata, string? formula = null, double[]? bbox = null)
    {
        RequireNodata(nodata);
        return new RasterAnalysisOptions(nodata, string.IsNullOrWhiteSpace(formula) ? null : formula.Trim(), bbox);
    }
}
