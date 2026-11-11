using System;
using System.Globalization;

namespace Registry.Web.Utilities;

/// <summary>
/// A size as the Hub writes it (<c>formatBytes</c> of <c>libs/format.js</c>): binary units, at most two decimals
/// without trailing zeros, digits grouped by thousands. The texts the server writes for the Hub (the 507 text of the
/// storage quota) use it, so a size reads the same on both sides.
/// </summary>
public static class ByteFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB"];

    /// <summary>The size <paramref name="bytes"/> as text, for example <c>1.5 MB</c>, <c>0 B</c> or <c>12 GB</c>.</summary>
    public static string Format(long bytes)
    {
        if (bytes == 0)
            return "0 B";

        var magnitude = Math.Abs((double)bytes);
        var unit = Math.Clamp((int)Math.Floor(Math.Log(magnitude) / Math.Log(1024)), 0, Units.Length - 1);
        var value = bytes / Math.Pow(1024, unit);
        return value.ToString("#,##0.##", CultureInfo.InvariantCulture) + " " + Units[unit];
    }
}
