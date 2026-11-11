using NUnit.Framework;
using Registry.Ports.Import;
using Registry.Web.Services.Import;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The slug an import makes from a name is the slug <c>ToSlug</c> makes (FR-ORG-05): accents dropped, ø to o, a run of
/// anything else a single dash, within the slug limits, never ending with a dash; empty when the name has no Latin
/// letter or digit (the import then picks a random slug).
/// </summary>
[TestFixture]
public class ImportDatasetNamingTests
{
    [TestCase("Ørsted Café Ü", "orsted-cafe-u")]
    [TestCase("  My Survey  2026 ", "my-survey-2026")]
    [TestCase("Straße", "strasse")]
    [TestCase("a", "a0")]
    public void Slugify_IsTheSlugOfTheName(string name, string expected) =>
        ImportDatasetNaming.Slugify(name).ShouldBe(expected);

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("!!!")]
    [TestCase("Москва")]
    public void Slugify_OfANameWithoutALatinLetterOrDigit_IsEmpty(string? name) =>
        ImportDatasetNaming.Slugify(name).ShouldBeEmpty();

    [Test]
    public void SlugOf_OfAName_WithoutALatinLetter_GetsARandomSlug_AndTheRequestedSlugWins()
    {
        var probe = new ImportSourceProbe(true, null, null, null, null);

        ImportDatasetNaming.SlugOf(null, probe, "Москва").ShouldStartWith("imported-");
        ImportDatasetNaming.SlugOf("moskva", probe, "Москва").ShouldBe("moskva");
    }

    // A source decides how long its suggestion is (a long page title or file name): the name is cut to 256 characters
    [Test]
    public void NameOf_CutsTheSuggestionOfTheSource_To256Characters()
    {
        var probe = new ImportSourceProbe(true, null, null, null, new string('a', 300));

        ImportDatasetNaming.NameOf(null, probe).ShouldBe(new string('a', 256));
        ImportDatasetNaming.NameOf(" ", probe).Length.ShouldBe(256);
        ImportDatasetNaming.NameOf("My name", probe).ShouldBe("My name");
    }

    [Test]
    public void NameOf_KeepsASuggestionOf256Characters_AndOneOf255()
    {
        ImportDatasetNaming.NameOf(null, new ImportSourceProbe(true, null, null, null, new string('b', 256))).ShouldBe(new string('b', 256));
        ImportDatasetNaming.NameOf(null, new ImportSourceProbe(true, null, null, null, new string('b', 255))).ShouldBe(new string('b', 255));
    }

    // U+1F600 takes two UTF-16 units: 255 letters and one of them are 257 units, the cut drops it whole
    [Test]
    public void NameOf_DoesNotSplitACharacterOfTwoUnits_WhenCuttingTheSuggestion()
    {
        var suggestion = new string('c', 255) + "\U0001F600" + "tail";
        var name = ImportDatasetNaming.NameOf(null, new ImportSourceProbe(true, null, null, null, suggestion));

        name.ShouldBe(new string('c', 255));

        var fits = new string('c', 254) + "\U0001F600" + "tail";
        ImportDatasetNaming.NameOf(null, new ImportSourceProbe(true, null, null, null, fits))
            .ShouldBe(new string('c', 254) + "\U0001F600");
    }

    [Test]
    public void SlugOf_OfALongSuggestion_IsTheSlugOfTheCutName()
    {
        var probe = new ImportSourceProbe(true, null, null, null, new string('d', 300));
        var name = ImportDatasetNaming.NameOf(null, probe);

        ImportDatasetNaming.SlugOf(null, probe, name).ShouldBe(new string('d', 128));
    }
}
