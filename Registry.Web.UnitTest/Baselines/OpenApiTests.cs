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

    // ---- The reference a client reads (FR-DEV-08, FR-DEV-12) ----

    private async Task<JsonObject> DocumentAsync() =>
        JsonNode.Parse(await _client.GetStringAsync("/swagger/v1/swagger.json"))!.AsObject();

    private static IEnumerable<(string Key, JsonObject Operation)> Operations(JsonObject document) =>
        from path in document["paths"]!.AsObject()
        from method in path.Value!.AsObject()
        select ($"{method.Key.ToUpperInvariant()} {path.Key}", method.Value!.AsObject());

    private static JsonObject Operation(JsonObject document, string method, string path) =>
        document["paths"]![path]![method]!.AsObject();

    [Test]
    public async Task SecuritySchemes_AreBearerAndBasic()
    {
        var schemes = (await DocumentAsync())["components"]!["securitySchemes"]!.AsObject();

        schemes["bearer"]!["type"]!.GetValue<string>().ShouldBe("http");
        schemes["bearer"]!["scheme"]!.GetValue<string>().ShouldBe("bearer");
        schemes["bearer"]!["bearerFormat"]!.GetValue<string>().ShouldBe("JWT");
        schemes["basic"]!["type"]!.GetValue<string>().ShouldBe("http");
        schemes["basic"]!["scheme"]!.GetValue<string>().ShouldBe("basic");
    }

    [Test]
    public async Task AdminOperation_ListsItsDenialsAndNeedsABearerToken()
    {
        var getUsers = Operation(await DocumentAsync(), "get", "/users");

        getUsers["responses"]!["401"]!["content"]!["application/problem+json"].ShouldNotBeNull();
        getUsers["responses"]!["403"]!["content"]!["application/problem+json"].ShouldNotBeNull();
        var security = getUsers["security"]!.AsArray();
        security.Count.ShouldBe(1);
        security[0]!.AsObject().Select(s => s.Key).ShouldBe(["bearer"]);
    }

    [Test]
    public async Task DatasetOperation_TakesATokenOrNothingAndListsTheAccessDenials()
    {
        var submit = Operation(await DocumentAsync(), "post", "/orgs/{orgSlug}/ds/{dsSlug}/tasks");

        // Public datasets are read without credentials: no requirement is one of the alternatives
        submit["security"]!.AsArray().Select(r => string.Join(",", r!.AsObject().Select(s => s.Key)))
            .ShouldBe(["", "bearer"]);
        submit["responses"]!["401"].ShouldNotBeNull();
        submit["responses"]!["403"].ShouldNotBeNull();
    }

    [Test]
    public async Task BasicAuthOperation_AlsoTakesBasicCredentials()
    {
        var buildFile = Operation(await DocumentAsync(), "get", "/orgs/{orgSlug}/ds/{dsSlug}/build/{hash}/{path}");

        buildFile["security"]!.AsArray().Select(r => string.Join(",", r!.AsObject().Select(s => s.Key)))
            .ShouldBe(["", "bearer", "basic"]);
    }

    [Test]
    public async Task AnonymousOperation_HasNoSecurityRequirement()
    {
        var authenticate = Operation(await DocumentAsync(), "post", "/users/authenticate");

        authenticate.ContainsKey("security").ShouldBeFalse();
    }

    [Test]
    public async Task ErrorResponses_AreProblemJson()
    {
        var wrong = Operations(await DocumentAsync())
            .SelectMany(o => o.Operation["responses"]!.AsObject()
                .Where(r => int.Parse(r.Key, System.Globalization.CultureInfo.InvariantCulture) >= 400 &&
                            r.Value!["content"] is JsonObject content && content.ContainsKey("application/json") &&
                            content["application/json"]!["schema"]?["$ref"]?.GetValue<string>().EndsWith("ProblemDetails") == true)
                .Select(r => $"{o.Key} {r.Key}"))
            .ToList();

        wrong.ShouldBeEmpty();
    }

    [TestCase("post", "/orgs/{orgSlug}/ds/{dsSlug}/obj", "file")]
    [TestCase("post", "/orgs/{orgSlug}/ds/{dsSlug}/obj/chunk", "chunk")]
    [TestCase("post", "/orgs/{orgSlug}/ds/{dsSlug}/obj/chunk/{fileId}/commit", "overwrite")]
    [TestCase("post", "/share/upload/{token}", "file")]
    [TestCase("post", "/share/upload-chunk/{token}", "chunk")]
    public async Task Upload_DescribesItsMultipartFields(string method, string path, string field)
    {
        var operation = Operation(await DocumentAsync(), method, path);

        var schema = operation["requestBody"]!["content"]!["multipart/form-data"]!["schema"]!;
        var properties = schema["properties"]!.AsObject();
        properties.ContainsKey(field).ShouldBeTrue($"{method} {path}: {string.Join(", ", properties.Select(p => p.Key))}");
    }

    [Test]
    public async Task UploadProbe_TakesARawBody()
    {
        var probe = Operation(await DocumentAsync(), "post", "/sys/upload-probe");

        probe["requestBody"]!["content"]!["application/octet-stream"]!["schema"]!["format"]!.GetValue<string>()
            .ShouldBe("binary");
    }

    [Test]
    public async Task UploadFile_IsBinary()
    {
        var upload = Operation(await DocumentAsync(), "post", "/orgs/{orgSlug}/ds/{dsSlug}/obj");

        var file = upload["requestBody"]!["content"]!["multipart/form-data"]!["schema"]!["properties"]!["file"]!;
        file["format"]!.GetValue<string>().ShouldBe("binary");
    }

    [Test]
    public async Task EveryOperation_HasAShortSummary()
    {
        var operations = Operations(await DocumentAsync()).ToList();

        // An operation title: one short line of plain text (Scalar shows Markdown backticks raw in titles)
        operations.Select(o => (o.Key, Summary: o.Operation["summary"]?.GetValue<string>() ?? ""))
            .Where(o => string.IsNullOrWhiteSpace(o.Summary) || o.Summary.Length > 100 || o.Summary.Contains('\n') ||
                        o.Summary.Contains('`') || o.Summary.Contains("Registry.Web."))
            .Select(o => $"{o.Key}: {o.Summary}").ShouldBeEmpty();
    }

    [Test]
    public async Task Descriptions_HaveNoClassNamesOrUnresolvedReferences()
    {
        var document = await DocumentAsync();
        var texts = Operations(document)
            .Select(o => (o.Key, Text: $"{o.Operation["summary"]} {o.Operation["description"]}"))
            .Concat(document["tags"]!.AsArray()
                .Select(t => (Key: $"tag {t!["name"]}", Text: t["description"]?.GetValue<string>() ?? "")));

        texts.Where(t => t.Text.Contains("Registry.Web.") || t.Text.Contains("cref"))
            .Select(t => $"{t.Key}: {t.Text}").ShouldBeEmpty();
    }

    [Test]
    public async Task TaskEvents_NamesTheHeartbeatInSeconds()
    {
        var events = Operation(await DocumentAsync(), "get", "/orgs/{orgSlug}/ds/{dsSlug}/tasks/events");

        $"{events["summary"]} {events["description"]}".ShouldContain("every 25 seconds");
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
