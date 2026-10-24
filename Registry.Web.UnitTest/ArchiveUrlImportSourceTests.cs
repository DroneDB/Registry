#nullable enable
using NUnit.Framework;
using Registry.Adapters.Archives;
using Registry.Web.Services.Import;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The scratch file of an archive import keeps the archive's extension so the extractor picks the format: the longest
/// suffix of the extractor's own list (<c>IArchiveExtractor.SupportedExtensions</c>, the one rule) the URL ends with.
/// </summary>
[TestFixture]
public class ArchiveUrlImportSourceTests
{
    private static readonly SharpCompressArchiveExtractor Extractor = new();

    [TestCase("https://host/data/flight.zip", ".zip")]
    [TestCase("https://host/data/flight.ZIP", ".zip")]
    [TestCase("https://host/data/flight.tar.gz", ".tar.gz")]
    [TestCase("https://host/data/flight.tgz", ".tgz")]
    [TestCase("https://host/data/flight.tar.bz2", ".tar.bz2")]
    [TestCase("https://host/data/flight.tar.xz", ".tar.xz")]
    [TestCase("https://host/data/flight.tbz2", ".tbz2")]
    [TestCase("https://host/data/flight.gz", ".gz")]
    [TestCase("https://host/data/flight.7z?token=a.b", ".7z")]
    [TestCase("https://host/data/flight.docx", ".docx")]
    [TestCase("https://host/data/flight", ".zip")]
    public void ArchiveExt_IsTheLongestSuffixOfTheExtractorsList(string url, string expected) =>
        ArchiveUrlImportSource.ArchiveExt(url, Extractor.SupportedExtensions).ShouldBe(expected);

    [Test]
    public void ArchiveExt_PicksTheCompoundSuffixWhateverTheOrderOfTheList() =>
        ArchiveUrlImportSource.ArchiveExt("https://host/a.tar.gz", [".gz", ".tar", ".tar.gz"]).ShouldBe(".tar.gz");

    [Test]
    public void ArchiveExt_FollowsTheList_NotACopyOfIt() =>
        ArchiveUrlImportSource.ArchiveExt("https://host/a.tar.zst", [".zst", ".tar.zst"]).ShouldBe(".tar.zst");

    [TestCase("https://host/data/flight.zip", "flight")]
    [TestCase("https://host/data/flight.tar.gz", "flight")]
    [TestCase("https://host/data/flight.tar.bz2", "flight")]
    [TestCase("https://host/data/flight.tgz", "flight")]
    [TestCase("https://host/data/flight.v2.7z", "flight.v2")]
    [TestCase("https://host/data/", "imported-dataset")]
    public void SuggestName_StripsTheLongestSuffixOfTheExtractorsList(string url, string expected) =>
        ArchiveUrlImportSource.SuggestName(new System.Uri(url), Extractor.SupportedExtensions).ShouldBe(expected);
}
