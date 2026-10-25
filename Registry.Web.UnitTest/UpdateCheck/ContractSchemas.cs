#nullable enable
using System.IO;
using System.Linq;
using System.Text.Json;
using Json.Schema;
using Registry.Web.Test.Support;

namespace Registry.Web.Test.UpdateCheck;

/// <summary>The JSON Schemas of the update check contract (<c>Registry.Web/Contracts</c>), to validate documents against.</summary>
internal sealed class ContractSchema
{
    private const string Folder = "Registry.Web/Contracts";

    public const string PayloadFile = "update-check-payload.v1.schema.json";
    public const string ResponseFile = "update-check-response.v1.schema.json";

    private readonly JsonSchema _schema;

    private ContractSchema(JsonSchema schema) => _schema = schema;

    public static ContractSchema Payload { get; } = Load(PayloadFile);

    public static ContractSchema Response { get; } = Load(ResponseFile);

    /// <summary>The path of a file of the contract folder or of its examples.</summary>
    public static string PathOf(string relativePath) => RepositoryFiles.PathOf(Path.Combine(Folder, relativePath));

    private static ContractSchema Load(string file) => new(JsonSchema.FromText(File.ReadAllText(PathOf(file))));

    /// <summary>The reasons <paramref name="json"/> does not match the schema (formats included); empty when it does.</summary>
    public string[] Violations(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = _schema.Evaluate(document.RootElement, new EvaluationOptions { RequireFormatValidation = true, OutputFormat = OutputFormat.Hierarchical });
        return result.IsValid ? [] : Collect(result).ToArray();
    }

    private static System.Collections.Generic.IEnumerable<string> Collect(EvaluationResults result)
    {
        if (result.Errors is { } errors)
            foreach (var (keyword, message) in errors)
                yield return $"{result.InstanceLocation} {keyword}: {message}";

        foreach (var detail in result.Details ?? [])
            foreach (var violation in Collect(detail))
                yield return violation;
    }
}
