using System.Text;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// Centralised helpers to map raw DroneDB layer / coverage names to XML-safe identifiers
/// (xs:NCName) required by WFS feature types, WCS coverage ids and gml:id attributes.
/// Kept here so every OGC manager and the layer catalog share the exact same algorithm.
/// </summary>
public static class OgcNames
{
    /// <summary>
    /// Sanitize an arbitrary string into a valid XML NCName:
    /// keeps letters, digits, '_', '-', '.'; replaces everything else with '_';
    /// prefixes with '_' when the first character is not a letter or underscore
    /// (NCNameStartChar forbids digits, '-' and '.').
    /// </summary>
    public static string ToNcName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "_unnamed";
        var sb = new StringBuilder(name.Length + 1);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.Append(c);
            else sb.Append('_');
        }
        var s = sb.ToString();
        if (!(char.IsLetter(s[0]) || s[0] == '_')) s = "_" + s;
        return s;
    }
}
