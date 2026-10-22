using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Registry.Adapters.Testing;

/// <summary>
/// One golden fixture of <c>Contract/fixtures/&lt;name&gt;.json</c>: a libddb call recorded on the
/// contract dataset and its normalised output (see <c>Contract/README.md</c>, "Normalisation").
/// </summary>
public sealed class ContractFixture
{
    private const string Folder = "fixtures/";
    private const string Extension = ".json";

    /// <summary>The <c>&lt;root&gt;</c> placeholder: the absolute dataset folder, generic form.</summary>
    public const string RootPlaceholder = "<root>";

    /// <summary>The value of a normalised volatile string key (meta <c>id</c>).</summary>
    public const string VolatileValue = "<volatile>";

    private readonly JToken _output;

    private ContractFixture(string name, JObject file)
    {
        Name = name;
        Call = (string?)file["call"] ?? throw new FormatException($"Fixture '{name}' has no call");
        Schema = (string?)file["schema"] ?? throw new FormatException($"Fixture '{name}' has no schema");
        VolatileKeys = file["normalisation"]?["volatileKeys"]?.Values<string>().OfType<string>().ToArray() ?? [];
        _output = file["output"] ?? throw new FormatException($"Fixture '{name}' has no output");
    }

    /// <summary>The fixture name: the file name without <c>.json</c>.</summary>
    public string Name { get; }

    /// <summary>The recorded libddb call, with <c>&lt;root&gt;</c> for the dataset folder.</summary>
    public string Call { get; }

    /// <summary>The schema file name, in <c>Contract/schemas/</c>.</summary>
    public string Schema { get; }

    /// <summary>The keys whose values were normalised (<c>depth</c>, <c>id</c>, <c>mtime</c>, <c>lastAttempt</c>, <c>retryAfter</c>).</summary>
    public IReadOnlyList<string> VolatileKeys { get; }

    /// <summary>A copy of the normalised output (callers may modify it).</summary>
    public JToken Output => _output.DeepClone();

    /// <summary>The names of every vendored fixture.</summary>
    public static IReadOnlyList<string> Names { get; } = ContractFiles.Paths
        .Where(p => p.StartsWith(Folder, StringComparison.Ordinal) && p.EndsWith(Extension, StringComparison.Ordinal))
        .Select(p => p[Folder.Length..^Extension.Length])
        .ToArray();

    /// <summary>Reads the fixture <paramref name="name"/>.</summary>
    public static ContractFixture Load(string name) => new(name, Parse(ContractFiles.ReadText(Folder + name + Extension)));

    /// <summary>
    /// Parses fixture JSON keeping every value as written: ISO dates stay strings (Json.NET would
    /// turn them into <see cref="DateTime"/> and write them back in another format).
    /// </summary>
    public static JObject Parse(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }
}
