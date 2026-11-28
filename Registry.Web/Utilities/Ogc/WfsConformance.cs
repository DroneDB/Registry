using System;
using System.Collections.Generic;
using System.Linq;
using Registry.Web.Exceptions;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// WFS 2.0 output formats (OGC 09-025r2): the one list the capabilities advertise per operation and the
/// requests are validated against, so the two cannot drift. A format this service does not produce is an
/// <c>InvalidParameterValue</c> exception with the locator <c>outputFormat</c> (§7.9.2.4.1).
/// </summary>
public static class WfsConformance
{
    /// <summary>The format of an answer when the request names none.</summary>
    public const string DefaultOutputFormat = "application/gml+xml; version=3.2";

    /// <summary>GML 3.2: the formats of DescribeFeatureType and GetPropertyValue, and of GetFeature as GML.</summary>
    public static readonly IReadOnlyList<string> GmlFormats = [DefaultOutputFormat, "text/xml; subtype=gml/3.2"];

    /// <summary>The formats of GetFeature (and of the GetFeatureById stored query): GML 3.2 or GeoJSON.</summary>
    public static readonly IReadOnlyList<string> FeatureFormats =
        [.. GmlFormats, "application/json", "application/json; subtype=geojson"];

    /// <summary>True when <paramref name="format"/> is one of the GML formats (media type compared without case or blanks).</summary>
    public static bool IsGml(string? format) => format != null && GmlFormats.Any(g => Same(g, format));

    /// <summary>
    /// Validates <paramref name="format"/> against <paramref name="allowed"/>; a missing format means the default.
    /// </summary>
    /// <exception cref="OgcException"><c>InvalidParameterValue</c>, locator <c>outputFormat</c>.</exception>
    public static void ValidateOutputFormat(string? format, IReadOnlyList<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(format)) return;
        if (allowed.Any(a => Same(a, format))) return;
        throw new OgcException("InvalidParameterValue",
            $"outputFormat '{format}' is not supported (expected one of [{string.Join(", ", allowed)}])",
            400, "outputFormat");
    }

    private static bool Same(string a, string b) =>
        string.Equals(Compact(a), Compact(b), StringComparison.Ordinal);

    private static string Compact(string format) =>
        string.Concat(format.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
}
