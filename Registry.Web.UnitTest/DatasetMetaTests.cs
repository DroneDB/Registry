using System.Collections.Generic;
using System.Text.Json;
using NUnit.Framework;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class DatasetMetaTests
{
    private const string MetaWithViews =
        """{"name":{"data":"Site"},"tagline":{"data":"T"},"views":[{"id":"a","data":{"name":"A"}}],"visibility":{"data":0}}""";

    private static Dictionary<string, JsonElement> PropertiesWithViews() =>
        TestJson.Props(("entries", 7), ("meta", TestJson.Parse(MetaWithViews)));

    [Test]
    public void WithoutViews_RemovesTheViewsAndKeepsEveryOtherKey()
    {
        var result = DatasetMeta.WithoutViews(PropertiesWithViews())!;

        var meta = result["meta"];
        meta.TryGetProperty(DatasetMeta.ViewsKey, out _).ShouldBeFalse();
        meta.GetProperty("name").GetProperty("data").GetString().ShouldBe("Site");
        meta.GetProperty("tagline").GetProperty("data").GetString().ShouldBe("T");
        meta.GetProperty("visibility").GetProperty("data").GetInt32().ShouldBe(0);
        result["entries"].GetInt32().ShouldBe(7);
    }

    [Test]
    public void WithoutViews_DoesNotChangeTheInput()
    {
        var input = PropertiesWithViews();

        var result = DatasetMeta.WithoutViews(input);

        result.ShouldNotBeSameAs(input);
        input["meta"].GetRawText().ShouldBe(TestJson.Parse(MetaWithViews).GetRawText());
        input["meta"].TryGetProperty("views", out var views).ShouldBeTrue();
        views.GetArrayLength().ShouldBe(1);
    }

    [Test]
    public void WithoutViews_MetaWithoutViewsPassesThrough()
    {
        var input = TestJson.Props(("meta", TestJson.Parse("""{"name":{"data":"Site"}}""")));

        DatasetMeta.WithoutViews(input).ShouldBeSameAs(input);
    }

    [Test]
    public void WithoutViews_WithoutMetaPassesThrough()
    {
        var input = TestJson.Props(("entries", 7));

        DatasetMeta.WithoutViews(input).ShouldBeSameAs(input);
        DatasetMeta.WithoutViews(null).ShouldBeNull();
    }

    [Test]
    public void WithoutViews_MetaThatIsNotAnObjectPassesThrough()
    {
        var input = TestJson.Props(("meta", "views"));

        DatasetMeta.WithoutViews(input).ShouldBeSameAs(input);
    }

    [Test]
    public void WithoutViews_OnlyCutsTheViewsOfTheMeta()
    {
        // A property named like the key outside the meta is not a saved view list
        var input = TestJson.Props(("views", 1), ("meta", TestJson.Parse(MetaWithViews)));

        var result = DatasetMeta.WithoutViews(input)!;

        result["views"].GetInt32().ShouldBe(1);
        result["meta"].TryGetProperty("views", out _).ShouldBeFalse();
    }
}
