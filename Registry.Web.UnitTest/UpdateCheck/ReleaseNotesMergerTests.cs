#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Registry.Web.Services.UpdateCheck;
using Shouldly;

namespace Registry.Web.Test.UpdateCheck;

/// <summary>
/// The merge of the notes of a range of releases into one document, on the real notes of Registry 2.6.6 to 2.7.1 and on
/// small synthetic ones.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~ReleaseNotesMergerTests"
/// </summary>
[TestFixture]
public class ReleaseNotesMergerTests
{
    private const string CompareUrl = "https://github.com/DroneDB/Registry/compare/v2.6.5...v2.7.1";

    private static readonly JsonSerializerOptions FixtureJson = new(JsonSerializerDefaults.Web);

    private static ReleaseInfo[] Real { get; } = JsonSerializer.Deserialize<ReleaseInfo[]>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UpdateCheck", "Fixtures", "releases-2.6.6-2.7.1.json")), FixtureJson)!;

    private static ReleaseInfo Release(string version, string notes) =>
        new(version, "2026-10-01", $"https://github.com/DroneDB/Registry/releases/tag/v{version}", notes);

    private static string[] Headings(string markdown) =>
        markdown.Split('\n').Where(l => l.StartsWith("## ", StringComparison.Ordinal)).Select(l => l[3..]).ToArray();

    private static string[] TopLevelItems(string markdown) =>
        markdown.Split('\n').Where(l => Regex.IsMatch(l, @"^[-*+] ")).ToArray();

    [Test]
    public void TheFixture_HoldsTheFiveRealReleases()
    {
        Real.Select(r => r.Version).ShouldBe(["2.7.1", "2.7.0", "2.6.8", "2.6.7", "2.6.6"]);
        Real.ShouldAllBe(r => r.Notes.Contains("**Full Changelog**", StringComparison.Ordinal));
    }

    [Test]
    public void TheSections_FollowTheFixedOrder_ThenTheOtherHeadingsInTheOrderTheyAreMet()
    {
        var merged = ReleaseNotesMerger.Merge(Real, CompareUrl, releasesTruncated: false);

        Headings(merged.Markdown).ShouldBe(["Breaking changes", "Highlights", "Added / Changed", "Fixed", "Merged PRs"]);
    }

    [Test]
    public void EveryTopLevelItem_EndsWithTheVersionThatMadeIt()
    {
        var merged = ReleaseNotesMerger.Merge(Real, CompareUrl, releasesTruncated: false);

        var expected = Real.SelectMany(r => TopLevelItems(r.Notes).Select(item => $"{item} (v{r.Version})")).ToList();
        var actual = TopLevelItems(merged.Markdown);

        actual.Length.ShouldBe(expected.Count);
        actual.OrderBy(l => l, StringComparer.Ordinal).ShouldBe(expected.OrderBy(l => l, StringComparer.Ordinal));
        actual.ShouldAllBe(item => Regex.IsMatch(item, @" \(v\d+\.\d+\.\d+\)$"));
    }

    [Test]
    public void TheNewestVersionComesFirst_InEverySection()
    {
        var merged = ReleaseNotesMerger.Merge(Real, CompareUrl, releasesTruncated: false);

        foreach (var section in merged.Markdown.Split("\n## ").Skip(1).Take(4))
        {
            var versions = TopLevelItems(section).Select(l => Version.Parse(Regex.Match(l, @"\(v(\d+\.\d+\.\d+)\)$").Groups[1].Value)).ToList();
            versions.ShouldBe(versions.OrderByDescending(v => v).ToList(), section.Split('\n')[0]);
        }
    }

    [Test]
    public void TheMigrationItems_StayUnderTheirParent_WithoutAVersion()
    {
        var merged = ReleaseNotesMerger.Merge(Real, CompareUrl, releasesTruncated: false);
        var lines = merged.Markdown.Split('\n');

        var parent = Array.FindIndex(lines, l => l.StartsWith("- `/stac/collections` now returns one page", StringComparison.Ordinal));
        parent.ShouldBeGreaterThanOrEqualTo(0);
        lines[parent].ShouldEndWith("(v2.7.1)");
        lines.Skip(parent + 1).Take(4).ShouldBe(
        [
            "  - Migration: follow the `next` link in the response `links` until it is absent.",
            "  - Migration: or pass `limit` (max 100) and `offset` explicitly.",
            "  - Migration: use `numberMatched` to get the total count.",
            "  - Migration: requests with `limit` below 1 now return `400`."
        ]);

        // The one of 2.6.6 is nested too, and the one of 2.6.7 is a top-level item of its own
        merged.Markdown.ShouldContain("  - Migration: after deploying, rescan existing datasets");
        merged.Markdown.ShouldContain("- Migration: build build-lod via scripts/build-buildlod.{sh,ps1}");
    }

    [Test]
    public void TheFullChangelogLines_AreDropped_AndTheCompareLinkIsTheLastLine()
    {
        var merged = ReleaseNotesMerger.Merge(Real, CompareUrl, releasesTruncated: false);

        merged.Markdown.ShouldNotContain("**Full Changelog**");
        merged.Markdown.Split('\n').Last().ShouldBe($"All changes: {CompareUrl}");
    }

    [Test]
    public void ARangeWithoutASecuritySection_IsNotASecurityUpdate() =>
        ReleaseNotesMerger.Merge(Real, CompareUrl, releasesTruncated: false).IsSecurityUpdate.ShouldBeFalse();

    [Test]
    public void ASecuritySection_GoesFirst_AndMakesTheRangeASecurityUpdate()
    {
        var releases = Real.Prepend(Release("2.7.2", "## Fixed\n- A bug\n\n## Security\n- Closed a hole in the login\n")).ToArray();

        var merged = ReleaseNotesMerger.Merge(releases, CompareUrl, releasesTruncated: false);

        merged.IsSecurityUpdate.ShouldBeTrue();
        Headings(merged.Markdown).First().ShouldBe("Security");
        merged.Markdown.ShouldStartWith("## Security\n- Closed a hole in the login (v2.7.2)\n");
    }

    [Test]
    public void ASecuritySectionInAnOlderRelease_CountsToo()
    {
        var releases = new[] { Release("2.7.2", "## Fixed\n- A bug"), Release("2.7.1", "## SECURITY\n- Old hole")};

        var merged = ReleaseNotesMerger.Merge(releases, null, false);

        merged.IsSecurityUpdate.ShouldBeTrue();
        merged.Markdown.ShouldContain("## Security\n- Old hole (v2.7.1)");
    }

    [Test]
    public void AnEmptySecuritySection_IsNoSecurityUpdate()
    {
        var merged = ReleaseNotesMerger.Merge([Release("2.7.2", "## Security\n\n## Fixed\n- A bug")], null, false);

        merged.IsSecurityUpdate.ShouldBeFalse();
        Headings(merged.Markdown).ShouldBe(["Fixed"]);
    }

    [Test]
    public void TheHeadings_AreComparedIgnoringCase_AndKeepTheCanonicalSpelling()
    {
        var releases = new[]
        {
            Release("2.7.2", "## FIXED\n- b\n\n## added / changed\n- c"),
            Release("2.7.1", "## Fixed\n- a\n\n## breaking CHANGES\n- d")
        };

        var merged = ReleaseNotesMerger.Merge(releases, null, false);

        Headings(merged.Markdown).ShouldBe(["Breaking changes", "Added / Changed", "Fixed"]);
        merged.Markdown.ShouldContain("## Fixed\n- b (v2.7.2)\n- a (v2.7.1)");
    }

    [Test]
    public void AnEmptyRange_GivesNoNotes()
    {
        var merged = ReleaseNotesMerger.Merge([], CompareUrl, releasesTruncated: false);

        merged.Markdown.ShouldBeEmpty();
        merged.IsSecurityUpdate.ShouldBeFalse();
    }

    [Test]
    public void ATruncatedRange_SaysSo_AndLinksTheComparison()
    {
        var merged = ReleaseNotesMerger.Merge(Real.Take(2).ToArray(), CompareUrl, releasesTruncated: true);

        merged.Markdown.Split('\n').Last().ShouldBe($"Older releases are not listed here. All changes: {CompareUrl}");
    }

    [Test]
    public void ATruncatedRange_WithoutALink_StillSaysSo() =>
        ReleaseNotesMerger.Merge([Release("2.7.2", "## Fixed\n- a")], null, releasesTruncated: true)
            .Markdown.Split('\n').Last().ShouldBe("Older releases are not listed here.");

    [Test]
    public void TheTextBeforeTheFirstHeading_GoesToOther()
    {
        var merged = ReleaseNotesMerger.Merge([Release("2.7.2", "Plain intro.\n- loose item\n\n## Fixed\n- a")], null, false);

        Headings(merged.Markdown).ShouldBe(["Fixed", "Other"]);
        merged.Markdown.ShouldContain("## Other\nPlain intro.\n- loose item (v2.7.2)");
    }

    [Test]
    public void TheOrderOfTheInput_DoesNotMatter()
    {
        var forward = ReleaseNotesMerger.Merge(Real, CompareUrl, false);
        var backward = ReleaseNotesMerger.Merge(Real.Reverse().ToArray(), CompareUrl, false);

        backward.Markdown.ShouldBe(forward.Markdown);
    }

    [Test]
    public void ASubHeading_AndAHeadingInsideACodeBlock_AreNotSections()
    {
        var notes = "## Fixed\n- a\n\n### Details\nmore\n\n```\n## Not a heading\n```";

        var merged = ReleaseNotesMerger.Merge([Release("2.7.2", notes)], null, false);

        merged.Markdown.ShouldBe("## Fixed\n- a (v2.7.2)\n\n### Details\nmore\n\n```\n## Not a heading\n```");
    }

    [Test]
    public void AScriptInTheNotes_IsKeptAsText_ForTheRendererToSanitize()
    {
        var merged = ReleaseNotesMerger.Merge([Release("2.7.2", "## Fixed\n- <script>alert(1)</script>")], null, false);

        merged.Markdown.ShouldContain("- <script>alert(1)</script> (v2.7.2)");
    }

    [Test]
    public void WindowsLineEndings_AreHandled()
    {
        var merged = ReleaseNotesMerger.Merge([Release("2.7.2", "## Fixed\r\n- a\r\n- b\r\n")], null, false);

        merged.Markdown.ShouldBe("## Fixed\n- a (v2.7.2)\n- b (v2.7.2)");
    }
}
