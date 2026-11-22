using System.Collections.Generic;
using System.Linq;
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

    private const string MetaWithReports =
        """{"name":{"data":"Site"},"reports":[{"id":"r_k3x9q2m7","data":{"name":"North pit"}}],"visibility":{"data":0}}""";

    private static Dictionary<string, JsonElement> PropertiesWithViews() =>
        TestJson.Props(("entries", 7), ("meta", TestJson.Parse(MetaWithViews)));

    [Test]
    public void WithoutHiddenKeys_RemovesTheViewsAndKeepsEveryOtherKey()
    {
        var result = DatasetMeta.WithoutHiddenKeys(PropertiesWithViews())!;

        var meta = result["meta"];
        meta.TryGetProperty(DatasetMeta.ViewsKey, out _).ShouldBeFalse();
        meta.GetProperty("name").GetProperty("data").GetString().ShouldBe("Site");
        meta.GetProperty("tagline").GetProperty("data").GetString().ShouldBe("T");
        meta.GetProperty("visibility").GetProperty("data").GetInt32().ShouldBe(0);
        result["entries"].GetInt32().ShouldBe(7);
    }

    [Test]
    public void WithoutHiddenKeys_DoesNotChangeTheInput()
    {
        var input = PropertiesWithViews();

        var result = DatasetMeta.WithoutHiddenKeys(input);

        result.ShouldNotBeSameAs(input);
        input["meta"].GetRawText().ShouldBe(TestJson.Parse(MetaWithViews).GetRawText());
        input["meta"].TryGetProperty("views", out var views).ShouldBeTrue();
        views.GetArrayLength().ShouldBe(1);
    }

    [Test]
    public void WithoutHiddenKeys_MetaWithoutHiddenKeysPassesThrough()
    {
        var input = TestJson.Props(("meta", TestJson.Parse("""{"name":{"data":"Site"}}""")));

        DatasetMeta.WithoutHiddenKeys(input).ShouldBeSameAs(input);
    }

    [Test]
    public void WithoutHiddenKeys_WithoutMetaPassesThrough()
    {
        var input = TestJson.Props(("entries", 7));

        DatasetMeta.WithoutHiddenKeys(input).ShouldBeSameAs(input);
        DatasetMeta.WithoutHiddenKeys(null).ShouldBeNull();
    }

    [Test]
    public void WithoutHiddenKeys_MetaThatIsNotAnObjectPassesThrough()
    {
        var input = TestJson.Props(("meta", "views"));

        DatasetMeta.WithoutHiddenKeys(input).ShouldBeSameAs(input);
    }

    [Test]
    public void WithoutHiddenKeys_RemovesTheReportsWhenThereAreNoViews()
    {
        var input = TestJson.Props(("entries", 7), ("meta", TestJson.Parse(MetaWithReports)));

        var result = DatasetMeta.WithoutHiddenKeys(input)!;

        result.ShouldNotBeSameAs(input);
        var meta = result["meta"];
        meta.TryGetProperty(DatasetMeta.ReportsKey, out _).ShouldBeFalse();
        meta.GetProperty("name").GetProperty("data").GetString().ShouldBe("Site");
        meta.GetProperty("visibility").GetProperty("data").GetInt32().ShouldBe(0);
        result["entries"].GetInt32().ShouldBe(7);
        input["meta"].TryGetProperty(DatasetMeta.ReportsKey, out _).ShouldBeTrue();
    }

    [Test]
    public void WithoutHiddenKeys_RemovesViewsAndReportsTogether()
    {
        var input = TestJson.Props(("meta", TestJson.Parse(
            """{"name":{"data":"Site"},"views":[{"id":"a"}],"reports":[{"id":"b"}]}""")));

        var meta = DatasetMeta.WithoutHiddenKeys(input)!["meta"];

        meta.EnumerateObject().Select(p => p.Name).ShouldBe(["name"]);
    }

    [Test]
    public void WithoutHiddenKeys_OnlyCutsTheViewsOfTheMeta()
    {
        // A property named like the key outside the meta is not a saved view list
        var input = TestJson.Props(("views", 1), ("meta", TestJson.Parse(MetaWithViews)));

        var result = DatasetMeta.WithoutHiddenKeys(input)!;

        result["views"].GetInt32().ShouldBe(1);
        result["meta"].TryGetProperty("views", out _).ShouldBeFalse();
    }
}
