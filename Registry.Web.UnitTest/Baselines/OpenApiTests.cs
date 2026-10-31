using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The API description (<c>/swagger/v1/swagger.json</c>) as a list of operations, each with its request body
/// and its responses (status, content type, schema), and the schemas with their member names. Recorded before
/// the action return types and the JSON stack change, so what the change adds (the types <c>ActionResult&lt;T&gt;</c>
/// derives, the responses of the OGC actions, the named records that replace anonymous objects) is reviewed in a
/// diff and nothing disappears unnoticed. Update on purpose: <c>REGISTRY_UPDATE_SNAPSHOTS=1</c>.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class OpenApiTests : IDisposable
{
    private const string SnapshotName = "wire/openapi-operations.txt";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    /// <summary>NUnit disposes the fixture after its last test.</summary>
    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Test]
    public async Task Operations_AreDescribedAsRecorded()
    {
        var document = JsonNode.Parse(await _client.GetStringAsync("/swagger/v1/swagger.json"))!.AsObject();
        var lines = new List<string>();

        foreach (var (path, item) in document["paths"]!.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal))
        foreach (var (method, operation) in item!.AsObject().OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            lines.Add($"{method.ToUpperInvariant()} {path}");
            if (operation!["requestBody"] is JsonObject body)
                lines.Add($"  body {Contents(body["content"])}");
            foreach (var (status, response) in operation["responses"]!.AsObject().OrderBy(r => r.Key, StringComparer.Ordinal))
                lines.Add($"  {status} {Contents(response!["content"])}".TrimEnd());
        }

        lines.Add("");
        foreach (var (name, schema) in document["components"]!["schemas"]!.AsObject().OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            var properties = (schema!["properties"] as JsonObject)?.Select(p => $"{p.Key}:{Describe(p.Value)}") ?? [];
            lines.Add($"schema {name} {{ {string.Join(", ", properties)} }}");
        }

        var failure = SnapshotFile.VerifyDocument(SnapshotName, string.Join('\n', lines));
        failure.ShouldBeNull(failure);
    }

    private static string Contents(JsonNode? content) =>
        content is JsonObject o
            ? string.Join("; ", o.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key} {Describe(c.Value?["schema"])}"))
            : "";

    private static string Describe(JsonNode? schema)
    {
        if (schema is not JsonObject o)
            return "any";
        if (o["$ref"] is { } reference)
            return reference.GetValue<string>().Split('/').Last();
        var type = o["type"]?.ToString() ?? (o.ContainsKey("oneOf") ? "oneOf" : o.ContainsKey("allOf") ? "allOf" : "any");
        if (type == "array")
            return $"array<{Describe(o["items"])}>";
        if (o["additionalProperties"] is JsonObject map && type == "object")
            return $"map<{Describe(map)}>";
        var format = o["format"]?.ToString();
        return format == null ? type : $"{type}({format})";
    }
}
