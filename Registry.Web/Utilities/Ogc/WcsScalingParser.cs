using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Registry.Web.Exceptions;
using Registry.Web.Services.Managers.Wcs;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// Parses the WCS 2.0 Scaling extension of a GetCoverage KVP request (OGC 12-039): <c>SCALEFACTOR=f</c>,
/// <c>SCALEAXES=axis(f),axis(f)</c> and <c>SCALESIZE=axis(n),axis(n)</c>. It returns the size, in pixels, of the output
/// of a region of <paramref name="native"/> pixels; the check against the limit is not its job
/// (<see cref="WcsCoverageService.EnsureWithinLimit"/>). At most one of the three may be present; an axis the request
/// does not name keeps its native size.
/// <para>
/// The errors are those of Table 7 of OGC 12-039 (HTTP 404): <c>InvalidScaleFactor</c> for a factor that is not a number
/// or is not positive, <c>ScaleAxisUndefined</c> for an axis the coverage does not have. The standard has no code for
/// the other errors (a size that is not a positive integer, an axis named twice, a malformed or a combined parameter):
/// they stay <c>InvalidParameterValue</c> (HTTP 400, WCS 2.0 core, OGC 09-110r4). <c>InvalidExtent</c> of the table
/// is for <c>SCALEEXTENT</c>, which this server does not implement.
/// </para>
/// </summary>
public static class WcsScalingParser
{
    /// <summary>OGC 12-039 Table 7: the scale factor is not a number, or is not greater than zero.</summary>
    public const string InvalidScaleFactor = "InvalidScaleFactor";

    /// <summary>OGC 12-039 Table 7: the axis of the scaling is not an axis of the coverage.</summary>
    public const string ScaleAxisUndefined = "ScaleAxisUndefined";

    private static readonly HashSet<string> ColumnAxes =
        new(WcsSubsetParser.LongAliases, StringComparer.OrdinalIgnoreCase) { "i" };

    private static readonly HashSet<string> RowAxes =
        new(WcsSubsetParser.LatAliases, StringComparer.OrdinalIgnoreCase) { "j" };

    /// <summary>
    /// The output size, or <c>null</c> when the request has no scaling parameter (the native size).
    /// </summary>
    /// <exception cref="OgcException">A parameter is malformed, names an unknown axis, or more than one is present.</exception>
    public static (int Width, int Height)? Parse(string? scaleFactor, string? scaleAxes, string? scaleSize,
        (int Width, int Height) native)
    {
        var present = new[] { scaleFactor, scaleAxes, scaleSize }.Count(v => !string.IsNullOrWhiteSpace(v));
        if (present == 0) return null;
        if (present > 1)
            throw new OgcException("InvalidParameterValue",
                "SCALEFACTOR, SCALEAXES and SCALESIZE cannot be combined; send one of them", 400, "scaling");

        if (!string.IsNullOrWhiteSpace(scaleFactor))
        {
            var factor = ParseFactor(scaleFactor, "SCALEFACTOR");
            return (Scale(native.Width, factor), Scale(native.Height, factor));
        }

        var width = native.Width;
        var height = native.Height;
        var seen = new HashSet<bool>();
        var isSize = !string.IsNullOrWhiteSpace(scaleSize);
        var name = isSize ? "SCALESIZE" : "SCALEAXES";
        foreach (var (axis, value) in Pairs(isSize ? scaleSize! : scaleAxes!, name))
        {
            var isColumn = ColumnAxes.Contains(axis);
            if (!isColumn && !RowAxes.Contains(axis))
                throw new OgcException(ScaleAxisUndefined,
                    $"{name} axis '{axis}' is not one of the coverage axis labels (Long, Lat)", 404, name);
            if (!seen.Add(isColumn))
                throw new OgcException("InvalidParameterValue", $"{name} names the axis '{axis}' twice", 400, name);

            int size;
            if (isSize)
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out size) || size < 1)
                    throw new OgcException("InvalidParameterValue",
                        $"{name} size '{value}' of axis '{axis}' is not a positive integer", 400, name);
            }
            else
            {
                size = Scale(isColumn ? native.Width : native.Height, ParseFactor(value, name));
            }

            if (isColumn) width = size;
            else height = size;
        }

        return (width, height);
    }

    private static int Scale(int pixels, double factor) =>
        (int)Math.Min(int.MaxValue, Math.Max(1, Math.Round(pixels * factor)));

    private static double ParseFactor(string raw, string name)
    {
        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var factor)
            || !double.IsFinite(factor) || factor <= 0)
            throw new OgcException(InvalidScaleFactor,
                $"{name} value '{raw}' is not a positive number", 404, name);
        return factor;
    }

    // "x(256),y(256)" -> [("x","256"), ("y","256")]
    private static IEnumerable<(string Axis, string Value)> Pairs(string raw, string name)
    {
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var open = part.IndexOf('(');
            if (open <= 0 || !part.EndsWith(')'))
                throw new OgcException("InvalidParameterValue",
                    $"{name} '{raw}' is malformed; expected axis(value),axis(value)", 400, name);
            yield return (part[..open].Trim(), part[(open + 1)..^1].Trim());
        }
    }
}
