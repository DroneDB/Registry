using System.Text.Json.Nodes;
using Registry.Ports.DroneDB;

namespace Registry.Adapters.Testing;

/// <summary>
/// One golden vector of libddb's dataset path rule (<c>Contract/vectors/entry-paths.json</c>,
/// <c>Contract/README.md</c> "Path vectors").
/// </summary>
/// <param name="Input">The path.</param>
/// <param name="Reserved">The names reserved as the first segment (<c>reservedJson</c>); null when absent.</param>
/// <param name="CString">False for an input with a NUL byte, which the C API cannot receive.</param>
/// <param name="Expected">The exact answer of <c>DDBNormalizePath</c>.</param>
/// <param name="Note">The vector's note, for test names.</param>
public sealed record PathVector(string Input, IReadOnlyList<string>? Reserved, bool CString,
    NormalizedEntryPath Expected, string? Note)
{
    /// <summary>A readable test name: the input with control characters escaped, and the reserved list.</summary>
    public override string ToString()
    {
        var input = string.Concat(Input.Select(c => char.IsControl(c) ? $"\\u{(int)c:X4}" : c.ToString()));
        return Reserved == null ? $"\"{input}\"" : $"\"{input}\" reserved [{string.Join(", ", Reserved)}]";
    }
}

/// <summary>The vendored golden path vectors (XI-09), parsed.</summary>
public static class PathVectors
{
    /// <summary>The vectors file, relative to <c>Contract/</c>.</summary>
    public const string FileName = "vectors/entry-paths.json";

    private static readonly Lazy<PathVector[]> Vectors = new(Load);

    /// <summary>Every vector, in file order.</summary>
    public static IReadOnlyList<PathVector> All => Vectors.Value;

    private static PathVector[] Load()
    {
        var file = ContractFixture.Parse(ContractFiles.ReadText(FileName));
        var vectors = file["vectors"] as JsonArray ?? throw new FormatException($"{FileName} has no 'vectors' array");

        return vectors.OfType<JsonObject>().Select(v =>
        {
            var input = (string?)v["input"] ?? throw new FormatException($"{FileName}: a vector has no input");
            var expected = v["expected"] as JsonObject ?? throw new FormatException($"{FileName}: '{input}' has no expected");
            var path = (string?)expected["path"];
            var rejected = (string?)expected["rejected"];

            return new PathVector(
                input,
                v["reserved"] is JsonArray reserved ? reserved.Select(r => (string?)r).OfType<string>().ToArray() : null,
                (bool?)v["cString"] ?? true,
                rejected != null ? NormalizedEntryPath.Rejection(rejected)
                    : NormalizedEntryPath.Accepted(path ?? throw new FormatException($"{FileName}: '{input}' expects nothing")),
                (string?)v["note"]);
        }).ToArray();
    }
}
