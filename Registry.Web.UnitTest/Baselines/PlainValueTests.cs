#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Registry.Common;
using Registry.Test.Common;
using Registry.Web.Identity;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// <see cref="PlainValue"/>, the value semantics of the free-form dictionaries (<c>User.Metadata</c>): equality and
/// hash by content, and a deep copy; and <see cref="PlainValue.From"/> / <see cref="PlainValue.ObjectFrom"/>, which read a
/// request's or a login provider's JSON into those values and must give the kinds the stored column gives back.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~PlainValueTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class PlainValueTests
{
    private static Dictionary<string, object?> Map(params (string Key, object? Value)[] items) =>
        items.ToDictionary(i => i.Key, i => i.Value);

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static IEnumerable<TestCaseData> EqualPairs()
    {
        yield return new TestCaseData(null, null).SetName("null and null");
        yield return new TestCaseData("a", "a").SetName("equal strings");
        yield return new TestCaseData(1L, 1).SetName("a long and an int are the same integer");
        yield return new TestCaseData((byte)7, 7L).SetName("a byte and a long");
        yield return new TestCaseData(1.5, 1.5f).SetName("a double and a float");
        yield return new TestCaseData(true, true).SetName("booleans");
        yield return new TestCaseData(new List<object?> { "a", 1L }, new object?[] { "a", 1L }).SetName("a list and an array");
        yield return new TestCaseData(Map(("a", 1L), ("b", "x")), Map(("b", "x"), ("a", 1L))).SetName("dictionaries are unordered");
        yield return new TestCaseData(Map(("l", new List<object?> { Map(("k", 1L)) })),
            Map(("l", new List<object?> { Map(("k", 1L)) }))).SetName("nested equal values in other instances");
        yield return new TestCaseData(Json("""{"a":[1,"x",null,{"b":2.5}]}"""),
            Map(("a", new List<object?> { 1L, "x", null, Map(("b", 2.5)) }))).SetName("a JsonElement is what its JSON says");
        yield return new TestCaseData(new Dictionary<string, object?>(), new Dictionary<string, object?>()).SetName("empty dictionaries");
    }

    private static IEnumerable<TestCaseData> DifferentPairs()
    {
        yield return new TestCaseData(null, "").SetName("null and an empty string");
        yield return new TestCaseData("a", "A").SetName("strings are case sensitive");
        yield return new TestCaseData(1L, 1.0).SetName("an integer and a floating-point number are different");
        yield return new TestCaseData(1L, "1").SetName("a number and its text");
        yield return new TestCaseData(true, 1L).SetName("a boolean and a number");
        yield return new TestCaseData(new List<object?> { "a", "b" }, new List<object?> { "b", "a" }).SetName("lists are ordered");
        yield return new TestCaseData(new List<object?> { "a" }, new List<object?> { "a", "a" }).SetName("lists of other lengths");
        yield return new TestCaseData(Map(("a", 1L)), Map(("a", 2L))).SetName("a value differs");
        yield return new TestCaseData(Map(("a", 1L)), Map(("b", 1L))).SetName("a key differs");
        yield return new TestCaseData(Map(("a", 1L)), Map(("a", 1L), ("b", 2L))).SetName("a key more");
        yield return new TestCaseData(Map(("l", new List<object?> { Map(("k", 1L)) })),
            Map(("l", new List<object?> { Map(("k", 2L)) }))).SetName("a value deep inside differs");
        yield return new TestCaseData(new List<object?>(), new Dictionary<string, object?>()).SetName("a list and a dictionary");
        yield return new TestCaseData(Json("""{"a":1}"""), Map(("a", 1.0))).SetName("a JSON integer is not a floating-point number");
        yield return new TestCaseData("abc", new List<object?> { 'a', 'b', 'c' }).SetName("a string is not a list of characters");
    }

    [TestCaseSource(nameof(EqualPairs))]
    public void EqualValues_AreEqual_BothWays_AndHashAlike(object? a, object? b)
    {
        PlainValue.AreEqual(a, b).ShouldBeTrue();
        PlainValue.AreEqual(b, a).ShouldBeTrue();
        PlainValue.GetHashCode(a).ShouldBe(PlainValue.GetHashCode(b));
    }

    [TestCaseSource(nameof(DifferentPairs))]
    public void DifferentValues_AreNotEqual_BothWays(object? a, object? b)
    {
        PlainValue.AreEqual(a, b).ShouldBeFalse();
        PlainValue.AreEqual(b, a).ShouldBeFalse();
    }

    [Test]
    public void TheHashOfADictionary_DoesNotDependOnItsInsertionOrder()
    {
        var first = Map(("a", 1L), ("b", 2L), ("c", 3L));
        var second = Map(("c", 3L), ("a", 1L), ("b", 2L));

        PlainValue.GetHashCode(first).ShouldBe(PlainValue.GetHashCode(second));
    }

    [Test]
    public void TheDeepCopy_IsEqual_AndNothingIsSharedWithTheOriginal()
    {
        var original = Map(("roles", new List<object?> { "a", "b" }), ("n", Map(("k", new List<object?> { 1L }))));

        var copy = (Dictionary<string, object?>)PlainValue.Clone((object?)original)!;

        PlainValue.AreEqual(original, copy).ShouldBeTrue();
        ((List<object?>)original["roles"]!).Add("c");
        ((List<object?>)((Dictionary<string, object?>)original["n"]!)["k"]!).Add(2L);
        PlainValue.AreEqual(original, copy).ShouldBeFalse();
        ((List<object?>)copy["roles"]!).Count.ShouldBe(2);
        ((List<object?>)((Dictionary<string, object?>)copy["n"]!)["k"]!).Count.ShouldBe(1);
    }

    [Test]
    public void TheDeepCopyOfADictionary_KeepsItsComparer()
    {
        var original = new Dictionary<string, object>(System.StringComparer.OrdinalIgnoreCase) { ["Key"] = "v" };

        var copy = PlainValue.Clone(original);

        copy.ContainsKey("key").ShouldBeTrue();
    }

    [Test]
    public void AJsonElement_IsReadAsThePlainValuesTheColumnHolds()
    {
        var plain = (Dictionary<string, object?>)PlainValue.From(Json(
            """{"s":"2020-01-02T03:04:05Z","i":-7,"big":9223372036854775807,"d":1.5,"w":2.0,"e":1e3,"t":true,"f":false,"n":null,"l":[1,[2]],"o":{"k":"v"}}"""))!;

        plain["s"].ShouldBe("2020-01-02T03:04:05Z");
        plain["i"].ShouldBeOfType<long>().ShouldBe(-7L);
        plain["big"].ShouldBeOfType<long>().ShouldBe(long.MaxValue);
        plain["d"].ShouldBeOfType<double>().ShouldBe(1.5);
        plain["w"].ShouldBeOfType<double>().ShouldBe(2.0);
        plain["e"].ShouldBeOfType<double>().ShouldBe(1000.0);
        plain["t"].ShouldBe(true);
        plain["f"].ShouldBe(false);
        plain["n"].ShouldBeNull();
        plain["l"].ShouldBeOfType<List<object?>>().Count.ShouldBe(2);
        plain["o"].ShouldBeOfType<Dictionary<string, object?>>()["k"].ShouldBe("v");
    }

    private static IEnumerable<TestCaseData> Numbers()
    {
        yield return new TestCaseData("0", 0L).SetName("From: zero is a long");
        yield return new TestCaseData("-7", -7L).SetName("From: a negative whole number is a long");
        yield return new TestCaseData("9223372036854775807", long.MaxValue).SetName("From: the largest long is a long");
        yield return new TestCaseData("9223372036854775808", 9223372036854775808.0).SetName("From: a whole number above long is a double");
        yield return new TestCaseData("1.5", 1.5).SetName("From: a fraction is a double");
        yield return new TestCaseData("2.0", 2.0).SetName("From: a whole number written with a point is a double");
        yield return new TestCaseData("1e3", 1000.0).SetName("From: an exponent is a double");
    }

    [TestCaseSource(nameof(Numbers))]
    public void From_KeepsTheKindOfANumber(string json, object expected)
    {
        var plain = PlainValue.From(Json(json));

        plain.ShouldNotBeNull();
        plain.GetType().ShouldBe(expected.GetType());
        plain.ShouldBe(expected);
    }

    [Test]
    public void AWholeNumberReadWithObjectFrom_IsStoredAsWritten_AndReadBackUnchanged()
    {
        var metadata = PlainValue.ObjectFrom(Json("""{"maxStorage":10,"ratio":0.5,"nested":{"count":3}}"""))!;

        var stored = UserMetadataJson.Serialize(metadata);

        stored.ShouldBe("""{"maxStorage":10,"ratio":0.5,"nested":{"count":3}}""");
        // What EF compares on every load and save: a re-read value must not look changed
        UserMetadataJson.AreEqual(metadata, UserMetadataJson.Deserialize(stored)!).ShouldBeTrue();
        metadata["maxStorage"].ShouldBeOfType<long>().ShouldBe(10L);
    }

    [TestCase("[1,2]")]
    [TestCase("\"text\"")]
    [TestCase("3")]
    [TestCase("true")]
    [TestCase("null")]
    public void ObjectFrom_IsNull_ForAValueThatIsNotAnObject(string json)
    {
        PlainValue.ObjectFrom(Json(json)).ShouldBeNull();
    }

    [Test]
    public void IsSameAs_IsTheValueComparison_OfTwoDictionaries()
    {
        var stored = new Dictionary<string, object> { ["roles"] = new List<object> { "a", "b" } };
        var sent = new Dictionary<string, object> { ["roles"] = new List<object> { "a", "b" } };

        stored.IsSameAs(sent).ShouldBeTrue();
        sent["roles"] = new List<object> { "a" };
        stored.IsSameAs(sent).ShouldBeFalse();
    }
}
