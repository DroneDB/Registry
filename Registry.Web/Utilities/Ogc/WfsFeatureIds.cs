using System;
using System.Globalization;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// WFS feature ids: "&lt;feature type&gt;.&lt;fid&gt;", where the feature type is the layer name as an
/// NCName (<see cref="OgcNames.ToNcName"/>) and fid the GeoPackage feature id DroneDB returns
/// (OGR writes gml:id="&lt;ogr layer&gt;.&lt;fid&gt;"). The same feature keeps its id in every page,
/// so an id can be resolved without scanning the layer.
/// </summary>
public static class WfsFeatureIds
{
    /// <summary>The id of an OGR feature (gml:id) in the feature type <paramref name="featureType"/>.</summary>
    public static string FromOgr(string ogrId, string featureType)
    {
        var dot = ogrId.LastIndexOf('.');
        return dot > 0 ? featureType + ogrId[dot..] : featureType + "." + ogrId;
    }

    /// <summary>Splits a feature id into its feature type and fid; false when it has no numeric fid.</summary>
    public static bool TryParse(string? id, out string featureType, out long fid)
    {
        featureType = string.Empty;
        fid = 0;
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var trimmed = id.Trim();
        var dot = trimmed.LastIndexOf('.');
        if (dot <= 0 || !long.TryParse(trimmed.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture,
                out fid))
            return false;

        featureType = trimmed[..dot];
        return true;
    }
}
