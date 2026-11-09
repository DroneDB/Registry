using NUnit.Framework;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The slug that <c>share/init</c> derives from a dataset name (§7.1, FR-ORG-14): the <c>ToSlug</c> form, and the suffix
/// <c>-&lt;n&gt;</c> that replaces the end of the base so that the slug stays within <see cref="SlugRules.MaxLength"/>.
/// </summary>
[TestFixture]
public class SlugSuffixTests
{
    [TestCase("My Survey 2026", "my-survey-2026")]
    [TestCase("  Brighton   Beach  ", "brighton-beach")]
    [TestCase("a", "a0")]
    [TestCase("!!!", "00")]
    public void FromName_IsTheSlugOfTheName(string name, string expected) =>
        SlugSuffix.FromName(name).ShouldBe(expected);

    [Test]
    public void FromName_OfALongName_IsWithinTheLimitAndDoesNotEndWithADash()
    {
        // 127 letters, a space, then more: the cut at 128 leaves "...a-"
        var name = new string('a', 127) + " bbbb";

        var slug = SlugSuffix.FromName(name);

        slug.ShouldBe(new string('a', 127));
        slug.Length.ShouldBeLessThanOrEqualTo(SlugRules.MaxLength);
    }

    [Test]
    public void Candidate_OfOne_IsTheBase_AndOfTwoOrMore_AddsTheSuffix()
    {
        SlugSuffix.Candidate("my-survey", 1).ShouldBe("my-survey");
        SlugSuffix.Candidate("my-survey", 2).ShouldBe("my-survey-2");
        SlugSuffix.Candidate("my-survey", 13).ShouldBe("my-survey-13");
    }

    [Test]
    public void Candidate_OfATakenSlugOfTheLimitLength_KeepsTheLimitAndEndsWithTheSuffix()
    {
        var taken = new string('a', SlugRules.MaxLength);

        var next = SlugSuffix.Candidate(taken, 2);

        next.ShouldBe(new string('a', 126) + "-2");
        next.Length.ShouldBe(SlugRules.MaxLength);
        SlugRules.IsValid(next).ShouldBeTrue();
    }

    [Test]
    public void Candidate_StripsADashTheCutLeavesBeforeTheSuffix()
    {
        // The base is cut to 126 characters for "-2": it would end with '-', and "--" is not a slug
        var baseSlug = new string('a', 125) + "-bb";

        var next = SlugSuffix.Candidate(baseSlug, 2);

        next.ShouldBe(new string('a', 125) + "-2");
        SlugRules.IsValid(next).ShouldBeTrue();
    }

    [Test]
    public void Candidate_WithADoubleDigitSuffix_CutsTheBaseFurther()
    {
        var next = SlugSuffix.Candidate(new string('a', SlugRules.MaxLength), 10);

        next.ShouldBe(new string('a', 125) + "-10");
        next.Length.ShouldBe(SlugRules.MaxLength);
    }
}
