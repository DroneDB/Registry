#nullable enable
using Newtonsoft.Json;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <see cref="DatasetPath"/>, the checked dataset path of a request (RQ-11): made only by
/// <see cref="DatasetPathPolicy.Parse"/>, read from JSON bodies by <see cref="DatasetPathJsonConverter"/>.
/// </summary>
[TestFixture]
public class DatasetPathTests
{
    private sealed class Body
    {
        public DatasetPath? Path { get; set; }
    }

    private static Body? Read(string json) =>
        JsonConvert.DeserializeObject<Body>(json, new DatasetPathJsonConverter(TestPaths.Policy));

    [TestCase(null)]
    [TestCase("")]
    [TestCase("  ")]
    public void Parse_NothingSent_IsNoPath(string? raw) => TestPaths.Policy.Parse(raw).ShouldBeNull();

    [TestCase("a.tif", "a.tif")]
    [TestCase("./sub//a.tif", "sub/a.tif")]
    [TestCase(@"sub\a.tif", "sub/a.tif")]
    public void Parse_IsTheNormalizedPath(string raw, string normalized)
    {
        var path = TestPaths.Policy.Parse(raw).ShouldNotBeNull();

        path.ToString().ShouldBe(normalized);
        ((string?)path).ShouldBe(normalized);
        path.ShouldBe(TestPaths.Policy.Parse(normalized));
    }

    [TestCase("../a.tif", "traversal")]
    [TestCase("/a.tif", "absolute")]
    [TestCase(".ddb/x", "reserved")]
    [TestCase(".uploads/x", "reserved")]
    public void Parse_RefusedPath_IsTheRulesException(string raw, string rejection) =>
        Should.Throw<InvalidDatasetPathException>(() => TestPaths.Policy.Parse(raw)).Rejection.ShouldBe(rejection);

    [Test]
    public void Json_ReadsStringsNumbersAndNull()
    {
        Read("""{ "path": "./a.tif" }""")!.Path!.ToString().ShouldBe("a.tif");
        Read("""{ "path": 42 }""")!.Path!.ToString().ShouldBe("42");
        Read("""{ "path": null }""")!.Path.ShouldBeNull();
        Read("""{ "path": "" }""")!.Path.ShouldBeNull();
        Read("{ }")!.Path.ShouldBeNull();
    }

    [Test]
    public void Json_RefusedPath_ThrowsTheRulesException_AndAnObjectIsAJsonError()
    {
        Should.Throw<InvalidDatasetPathException>(() => Read("""{ "path": "../a.tif" }"""));
        Should.Throw<JsonSerializationException>(() => Read("""{ "path": { "a": 1 } }"""));
    }
}
