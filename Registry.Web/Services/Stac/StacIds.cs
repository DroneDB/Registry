#nullable enable
using System;
using System.Text;

namespace Registry.Web.Services.Stac;

/// <summary>
/// The STAC ids and URL segments Registry gives the entries of a dataset (libddb's data-only documents
/// have none). The STAC API names a feature by the URL-safe Base64 of its path; the legacy per-dataset documents
/// (<c>/orgs/{org}/ds/{ds}/stac</c>) keep the ids and links they always had.
/// </summary>
public static class StacIds
{
    /// <summary>The STAC API feature id of an entry: the URL-safe Base64 of its path, without padding.</summary>
    public static string FeatureId(string path) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(path)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// The path a URL segment names: Base64 in either alphabet (standard or URL-safe), with or without padding.
    /// False when it is not Base64 or not UTF-8.
    /// </summary>
    public static bool TryDecodePath(string? segment, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrEmpty(segment))
            return false;

        var s = segment.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 1: return false;
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        try
        {
            path = Encoding.UTF8.GetString(Convert.FromBase64String(s));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The legacy item URL segment of an entry: the standard Base64 of its path, as libddb wrote it. A path whose
    /// standard Base64 holds '/' or '+' is written in the URL-safe alphabet instead: the standard form cannot be
    /// one route segment ('/' splits it), so that link never worked (the route accepts both alphabets).
    /// </summary>
    public static string LegacySegment(string path)
    {
        var standard = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
        return standard.AsSpan().IndexOfAny('/', '+') < 0 ? standard : standard.Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// The legacy item id of an entry: "path-" and its path lowercased, ASCII letters, digits and '_' kept, every
    /// run of other bytes one '-', no '-' at either end (the rule libddb's STAC writer had; Registry owns it now).
    /// </summary>
    public static string LegacyItemId(string path)
    {
        var id = new StringBuilder("path-", path.Length + 5);
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z')
                c = (char)(c + ('a' - 'A'));

            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
                id.Append(c);
            else if (id[^1] != '-')
                id.Append('-');
        }

        return id.ToString().Trim('-');
    }
}
