using System;
using Registry.Ports.Import;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Import;

/// <summary>The name and slug of the dataset an import creates (out of the import manager).</summary>
public static class ImportDatasetNaming
{
    /// <summary>The name: the requested one, else the source's suggestion (<see cref="SuggestedNameOf"/>), else "Imported dataset".</summary>
    public static string NameOf(string? requestedName, ImportSourceProbe probe) =>
        !string.IsNullOrWhiteSpace(requestedName) ? requestedName : SuggestedNameOf(probe) ?? "Imported dataset";

    /// <summary>The tagline a source suggests (the tagline of a remote dataset), cut to the longest tagline a dataset has (<see cref="Adapters.SafeMetaManager.TaglineMaxLength"/> characters); null when it has none.</summary>
    public static string? TaglineOf(ImportSourceProbe probe) =>
        CutAt(string.IsNullOrWhiteSpace(probe.SuggestedTagline) ? null : probe.SuggestedTagline.Trim(),
            Adapters.SafeMetaManager.TaglineMaxLength);

    /// <summary>
    /// The name a source suggests (a file name, a page title), cut to the longest name a dataset has
    /// (<see cref="SlugRules.NameMaxLength"/> characters) without splitting a character that takes two UTF-16 units; the
    /// source decides how long it is, the Registry does not refuse the import for it.
    /// </summary>
    public static string? SuggestedNameOf(ImportSourceProbe probe) => CutName(probe.SuggestedName);

    /// <summary>A name cut to <see cref="SlugRules.NameMaxLength"/> characters, whole characters (a surrogate pair is kept or dropped as one).</summary>
    public static string? CutName(string? name) => CutAt(name, SlugRules.NameMaxLength);

    private static string? CutAt(string? text, int maxLength)
    {
        if (text is null || text.Length <= maxLength)
            return text;

        var length = maxLength;
        if (char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
            length--;

        return text[..length];
    }

    /// <summary>
    /// The slug a source suggests when it has a better one than the slug of the suggested name (the slug of the remote
    /// dataset), if it is a slug the Registry accepts; else null.
    /// </summary>
    public static string? SuggestedSlugOf(ImportSourceProbe probe) =>
        !string.IsNullOrWhiteSpace(probe.SuggestedSlug) && probe.SuggestedSlug.IsValidSlug() ? probe.SuggestedSlug : null;

    /// <summary>The slug: the requested one, else the slug of the suggestion or of <paramref name="name"/>, else "imported-" and a random suffix.</summary>
    public static string SlugOf(string? requestedSlug, ImportSourceProbe probe, string name)
    {
        var slug = !string.IsNullOrWhiteSpace(requestedSlug) ? requestedSlug : SuggestedSlugOf(probe) ?? Slugify(SuggestedNameOf(probe) ?? name);
        return string.IsNullOrWhiteSpace(slug) ? "imported-" + Guid.NewGuid().ToString("N")[..8] : slug;
    }

    /// <summary>
    /// The slug of a name, the one of <see cref="Extenders.ToSlug"/> (accents dropped, ø to o and the like, a run of anything else a
    /// single dash, no dash at either end, within the slug limits, FR-ORG-05); empty for a blank name and for one with no
    /// Latin letter or digit, which gets a random slug (<see cref="SlugOf"/>).
    /// </summary>
    public static string Slugify(string? value) =>
        InputRules.HasLatinLetterOrDigit(value) ? value!.Trim().ToSlug() : string.Empty;
}
