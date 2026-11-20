using System;

namespace Registry.Web.Utilities;

/// <summary>
/// The slug of a dataset made from its name when the request gives no slug (<c>share/init</c>, §7.1; FR-ORG-14): the
/// <see cref="Extenders.ToSlug"/> form, and, when that slug is taken, the same with the suffix <c>-&lt;n&gt;</c>
/// (the lowest free n of 2 or more). Both stay within <see cref="SlugRules.MaxLength"/> and never end with '-' or '_'.
/// </summary>
public static class SlugSuffix
{
    /// <summary>
    /// The slug made from <paramref name="name"/>: <see cref="Extenders.ToSlug"/> of the trimmed name, with a trailing '-' or '_' (what a cut
    /// at the length limit can leave) removed and the result padded to <see cref="SlugRules.MinLength"/>.
    /// </summary>
    public static string FromName(string name)
    {
        var slug = Strip(name.Trim().ToSlug());
        return slug.Length < SlugRules.MinLength ? slug.PadRight(SlugRules.MinLength, '0') : slug;
    }

    /// <summary>
    /// Candidate number <paramref name="n"/> of <paramref name="baseSlug"/>: the slug itself for 1, else the suffix
    /// <c>-&lt;n&gt;</c> replaces the end of the base, which is cut to <see cref="SlugRules.MaxLength"/> minus the
    /// suffix and stripped of a trailing '-' or '_' before it.
    /// </summary>
    public static string Candidate(string baseSlug, int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        if (n == 1)
            return baseSlug;

        var suffix = $"-{n}";
        var cut = baseSlug.Length > SlugRules.MaxLength - suffix.Length
            ? baseSlug[..(SlugRules.MaxLength - suffix.Length)]
            : baseSlug;
        cut = Strip(cut);
        return (cut.Length == 0 ? "0" : cut) + suffix;
    }

    private static string Strip(string slug) => slug.TrimEnd('-', '_');
}
