using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Registry.Web.Test.Rules;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.E2eFixes;

/// <summary>
/// End-to-end check: <c>IsValidSlug</c> accepted one character while
/// the routes (<c>^[a-z0-9][a-z0-9_-]{1,128}$</c>) need two, so <c>POST /orgs</c> with slug <c>o</c> stored the
/// organization, answered 500 (no route for the Location header) and left it unreachable. One definition
/// now (<see cref="SlugRules"/>) is used by the validator, <c>ToSlug</c> and the route constraint.
/// </summary>
[TestFixture]
public class SlugRulesTests
{
    [TestCase("o")]
    [TestCase("0")]
    [TestCase("-")]
    [TestCase("_")]
    [TestCase("")]
    [TestCase("-ab")]
    [TestCase("_ab")]
    [TestCase("Ab")]
    [TestCase("a b")]
    [TestCase("a/b")]
    [TestCase("a--b")]
    public void IsValidSlug_Refuses(string slug) => slug.IsValidSlug().ShouldBeFalse();

    [TestCase("ab")]
    [TestCase("a0")]
    [TestCase("00")]
    [TestCase("a-")]
    [TestCase("a_")]
    [TestCase("my-org_1")]
    public void IsValidSlug_Accepts(string slug) => slug.IsValidSlug().ShouldBeTrue();

    [Test]
    public void IsValidSlug_LengthBounds_AreTheRoutesBounds()
    {
        new string('a', SlugRules.MinLength - 1).IsValidSlug().ShouldBeFalse();
        new string('a', SlugRules.MinLength).IsValidSlug().ShouldBeTrue();
        new string('a', SlugRules.MaxLength).IsValidSlug().ShouldBeTrue();
        new string('a', SlugRules.MaxLength + 1).IsValidSlug().ShouldBeFalse();
        (SlugRules.MinLength, SlugRules.MaxLength).ShouldBe((2, 128));
    }

    // A slug the validator accepts must route; what routes but the validator refuses ("a--b") gets the
    // validator's 400 with its message instead of falling through to the SPA
    [Test]
    public void EverySlugTheValidatorAccepts_IsRoutable()
    {
        var samples = new[] { "ab", "a-b", "a_b", "0a", "my-org_1", new string('z', 128), "a-", "o0" };
        foreach (var slug in samples.Where(s => s.IsValidSlug()))
            SlugRules.IsWellFormed(slug).ShouldBeTrue(slug);

        SlugRules.IsWellFormed("o").ShouldBeFalse();
        SlugRules.IsWellFormed("a--b").ShouldBeTrue();
        SlugRules.IsWellFormed("A").ShouldBeFalse();
    }

    // The generated slug of a name (a user name, a dataset name) is a valid one, whatever the name
    [TestCase("a", "a0")]
    [TestCase("A", "a0")]
    [TestCase("é", "e0")]
    [TestCase("7", "70")]
    [TestCase("-", "00")]
    [TestCase("*", "00")]
    [TestCase("ab", "ab")]
    public void ToSlug_AlwaysMakesAValidSlug(string name, string expected)
    {
        var slug = name.ToSlug();

        slug.ShouldBe(expected);
        slug.IsValidSlug().ShouldBeTrue();
    }

    [Test]
    public void ToSlug_OfEverySingleCharacter_IsValid()
    {
        var failures = new List<string>();
        for (var c = (char)33; c < 0x250; c++)
        {
            if (char.IsWhiteSpace(c))
                continue; // no name at all: ToSlug refuses it
            var slug = c.ToString().ToSlug();
            if (!slug.IsValidSlug())
                failures.Add($"U+{(int)c:X4} -> '{slug}'");
        }

        failures.ShouldBeEmpty();
    }

    // The cases the Hub runs against libs/slug.js (Rules/input-rule-vectors.json) are the validator's answers
    [Test]
    public void SharedCases_AreTheValidatorsAnswers()
    {
        var accepted = InputRuleVectors.Values("slug", "accept");
        var rejected = InputRuleVectors.Values("slug", "reject");

        accepted.ShouldNotBeEmpty();
        rejected.ShouldNotBeEmpty();
        accepted.Where(s => !s.IsValidSlug()).ShouldBeEmpty();
        rejected.Where(s => s.IsValidSlug()).ShouldBeEmpty();
    }

    // The cases the Hub runs against slugFromName (libs/slug.js): one generator on both sides (FR-ORG-05, FR-ORG-14)
    [Test]
    public void SharedToSlugCases_AreWhatToSlugMakes()
    {
        var cases = InputRuleVectors.ToSlugCases();

        cases.ShouldNotBeEmpty();
        cases.Where(c => c.Name.ToSlug() != c.Slug).Select(c => $"'{c.Name}' -> '{c.Name.ToSlug()}', expected '{c.Slug}'")
            .ShouldBeEmpty();
    }

    [TestCase("Ørsted Café Ü", "orsted-cafe-u")]
    [TestCase("Équipe Été", "equipe-ete")]
    [TestCase("Straße", "strasse")]
    [TestCase("ÆØÅ Łódź", "aeoa-lodz")]
    public void ToSlug_TransliteratesTheLettersThatNfdLeavesWhole(string name, string expected) =>
        name.ToSlug().ShouldBe(expected);

    [Test]
    public void ToSlug_OfALongName_IsWithinTheLimit_AndDoesNotEndWithADashOrAnUnderscore()
    {
        // The cut at 128 leaves "...a-" and "...a_"
        var withDash = (new string('a', 127) + " bbbb").ToSlug();
        var withUnderscore = (new string('a', 127) + "_bbbb").ToSlug();
        var twoHundred = string.Join(' ', Enumerable.Repeat("word", 40)).ToSlug();

        withDash.ShouldBe(new string('a', 127));
        withUnderscore.ShouldBe(new string('a', 127));
        twoHundred.Length.ShouldBeLessThanOrEqualTo(SlugRules.MaxLength);
        twoHundred.ShouldNotEndWith("-");
        twoHundred.IsValidSlug().ShouldBeTrue();
    }

    [Test]
    public void ToSlug_OfAnExpandingTransliteration_StaysWithinTheLimit()
    {
        var slug = new string('ß', 100).ToSlug();

        slug.ShouldBe(new string('s', SlugRules.MaxLength));
    }

    [Test]
    public void NameLimits_AreTheOnesTheRegistryPublishes()
    {
        (SlugRules.NameMinLength, SlugRules.NameMaxLength, SlugRules.DescriptionMaxLength).ShouldBe((1, 256, 2000));
        var rules = InputRules.Describe();
        rules.DisplayName!.MaxLength.ShouldBe(256);
        rules.DisplayName.MinLength.ShouldBe(1);
        rules.DescriptionText!.MaxLength.ShouldBe(2000);
    }

    [Test]
    public void Description_SaysTheRulesToTheClient()
    {
        SlugRules.Description.ShouldContain("2 to 128");
        SlugRules.Description.ShouldContain("lowercase");
        SlugRules.InvalidMessage("Dataset").ShouldStartWith("Dataset slug is invalid: ");
    }
}
