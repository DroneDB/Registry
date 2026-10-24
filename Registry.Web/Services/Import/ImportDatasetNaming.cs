#nullable enable
using System;
using System.Text;
using Registry.Ports.Import;

namespace Registry.Web.Services.Import;

/// <summary>The name and slug of the dataset an import creates (RQ-12, out of the import manager).</summary>
public static class ImportDatasetNaming
{
    /// <summary>The name: the requested one, else the source's suggestion, else "Imported dataset".</summary>
    public static string NameOf(string? requestedName, ImportSourceProbe probe) =>
        !string.IsNullOrWhiteSpace(requestedName) ? requestedName : probe.SuggestedName ?? "Imported dataset";

    /// <summary>The slug: the requested one, else the slug of the suggestion or of <paramref name="name"/>, else "imported-" and a random suffix.</summary>
    public static string SlugOf(string? requestedSlug, ImportSourceProbe probe, string name)
    {
        var slug = !string.IsNullOrWhiteSpace(requestedSlug) ? requestedSlug : Slugify(probe.SuggestedName ?? name);
        return string.IsNullOrWhiteSpace(slug) ? "imported-" + Guid.NewGuid().ToString("N")[..8] : slug;
    }

    /// <summary>Lower case ASCII letters and digits, anything else (a run of it) a single dash, no dash at either end.</summary>
    public static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        var lastDash = false;
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) && c < 128)
            {
                sb.Append(c);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        return sb.ToString().Trim('-');
    }
}
