using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Registry.Web.Services.UpdateCheck;

/// <summary>The notes of a range of releases as one Markdown document.</summary>
/// <param name="Markdown">The merged notes; empty for an empty range.</param>
/// <param name="IsSecurityUpdate">A release of the range has a "Security" section.</param>
public sealed record MergedReleaseNotes(string Markdown, bool IsSecurityUpdate)
{
    /// <summary>No release, no notes.</summary>
    public static MergedReleaseNotes Empty { get; } = new(string.Empty, false);
}

/// <summary>
/// The one place that merges release notes, for the Hub and for the update e-mail: the notes of each release are split at
/// their <c>##</c> headings and the sections of all releases are joined, in a fixed order, newest release first, each
/// top-level item tagged with the version that introduced it.
/// </summary>
public static partial class ReleaseNotesMerger
{
    private const string SecurityKey = "security";
    private const string OtherTitle = "Other";

    // The sections every release of Registry uses, in the order they are shown; any other heading follows in the order it is met
    private static readonly (string Key, string Title)[] KnownSections =
    [
        (SecurityKey, "Security"),
        ("breaking changes", "Breaking changes"),
        ("highlights", "Highlights"),
        ("added / changed", "Added / Changed"),
        ("fixed", "Fixed")
    ];

    [GeneratedRegex(@"^##(?!#)\s*(?<title>\S.*?)\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(?:[-*+]|\d+[.)])\s")]
    private static partial Regex TopLevelItem();

    /// <summary>
    /// Merges the notes of <paramref name="releases"/>.
    /// </summary>
    /// <param name="releases">The releases of the range, in any order (they are shown newest first).</param>
    /// <param name="compareUrl">The page that lists all changes of the range; the last line of the notes links it.</param>
    /// <param name="releasesTruncated">The older releases of the range are not in <paramref name="releases"/>: the last line says so.</param>
    public static MergedReleaseNotes Merge(IReadOnlyList<ReleaseInfo> releases, string? compareUrl, bool releasesTruncated)
    {
        ArgumentNullException.ThrowIfNull(releases);
        if (releases.Count == 0)
            return MergedReleaseNotes.Empty;

        var sections = new List<Section>();
        foreach (var release in releases.OrderByDescending(r => ParseVersion(r.Version), Comparer<Version?>.Default))
            foreach (var (title, lines) in Split(release.Notes))
                Add(sections, title, Tag(lines, release.Version));

        var parts = new List<string>();
        foreach (var section in Ordered(sections).Where(s => s.Lines.Count > 0))
            parts.Add($"## {section.Title}\n{string.Join('\n', section.Lines)}");

        var footer = Footer(compareUrl, releasesTruncated);
        if (footer is not null)
            parts.Add(footer);

        var security = sections.Any(s => s.Key == SecurityKey && s.Lines.Count > 0);
        return new MergedReleaseNotes(string.Join("\n\n", parts), security);
    }

    private static string? Footer(string? compareUrl, bool truncated)
    {
        if (truncated)
            return string.IsNullOrWhiteSpace(compareUrl)
                ? "Older releases are not listed here."
                : $"Older releases are not listed here. All changes: {compareUrl}";

        return string.IsNullOrWhiteSpace(compareUrl) ? null : $"All changes: {compareUrl}";
    }

    private static Version? ParseVersion(string version) => Version.TryParse(version, out var parsed) ? parsed : null;

    // The sections of one release, in the order they appear; the text before the first heading is "Other"
    private static List<(string Title, List<string> Lines)> Split(string notes)
    {
        var result = new List<(string, List<string>)>();
        var current = (Title: OtherTitle, Lines: new List<string>());
        var inFence = false;

        void Flush()
        {
            if (current.Lines.Any(l => l.Trim().Length > 0))
                result.Add(current);
        }

        foreach (var raw in notes.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                inFence = !inFence;

            if (!inFence && Heading().Match(line) is { Success: true } heading)
            {
                Flush();
                current = (heading.Groups["title"].Value, []);
            }
            else if (!line.StartsWith("**Full Changelog**", StringComparison.Ordinal))
                current.Lines.Add(line);
        }

        Flush();
        return result;
    }

    // Tags each top-level item with the version; nested items and continuation lines stay under their parent
    private static List<string> Tag(List<string> lines, string version)
    {
        var tagged = new List<string>();
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                // A single blank line between two blocks; none at the edges
                if (tagged.Count > 0 && tagged[^1].Length > 0)
                    tagged.Add(string.Empty);
            }
            else
                tagged.Add(TopLevelItem().IsMatch(line) ? $"{line} (v{version})" : line);
        }

        while (tagged.Count > 0 && tagged[^1].Length == 0)
            tagged.RemoveAt(tagged.Count - 1);

        return tagged;
    }

    private static void Add(List<Section> sections, string title, List<string> lines)
    {
        var key = KeyOf(title);
        var section = sections.FirstOrDefault(s => s.Key == key);
        if (section is null)
        {
            var known = KnownSections.FirstOrDefault(k => k.Key == key);
            section = new Section(key, known.Title ?? title.Trim());
            sections.Add(section);
        }

        section.Lines.AddRange(lines);
    }

    private static IEnumerable<Section> Ordered(List<Section> sections) =>
        KnownSections.Select(k => sections.FirstOrDefault(s => s.Key == k.Key)).OfType<Section>()
            .Concat(sections.Where(s => KnownSections.All(k => k.Key != s.Key)));

    private static string KeyOf(string title) => Regex.Replace(title.Trim(), @"\s+", " ").ToLowerInvariant();

    private sealed record Section(string Key, string Title)
    {
        public List<string> Lines { get; } = [];
    }
}
