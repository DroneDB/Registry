#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-14: how MVC binds the JSON request bodies, pinned before the System.Text.Json switch (D3) and unchanged
/// after it. Every JSON-body DTO is bound from the payload the Hub's request builders send
/// (<c>ClientApp/webapp/js/libs/ddb/*.js</c>: members left <c>undefined</c> are absent, optional ones
/// <c>null</c>), through the host's own input formatter. A second group states what the formatter accepts
/// and refuses beyond that (member name casing, numbers and booleans in strings, enum names and numbers,
/// unknown members, comments, trailing commas, bad types), because Json.NET was lenient in each of them and
/// a client may depend on it. The outcome of each case (accepted or refused, and the bound object written back
/// by the host's output formatter) is compared with <c>Snapshots/wire/request-binding.json</c>, recorded on
/// Json.NET. The ddb CLI sends forms, not JSON (<c>DroneDB/src/library/*client*.cpp</c>): form binding does not
/// use the JSON stack, and the organization request tests of this item covers the form DTO this item changes.
/// Update on purpose: <c>REGISTRY_UPDATE_SNAPSHOTS=1</c>, then review the diff.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class Rq14RequestBindingTests
{
    private const string SnapshotName = "wire/request-binding.json";

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private RegistryWebFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _factory = new RegistryWebFactory();

    [OneTimeTearDown]
    public void OneTimeTearDown() => _factory.Dispose();

    public sealed record Case(string Name, Type Type, string Json)
    {
        public override string ToString() => Name;
    }

    /// <summary>The payloads the Hub sends, one per JSON-body DTO.</summary>
    private static IEnumerable<Case> HubPayloads()
    {
        // adminApi.js
        yield return new("hub-create-user", typeof(CreateUserRequest),
            """{"userName":"alice","email":"alice@example.com","password":"Secret123!","roles":["admin"]}""");
        yield return new("hub-create-user-no-roles", typeof(CreateUserRequest),
            """{"userName":"bob","email":"","password":"Secret123!","roles":[]}""");
        yield return new("hub-update-user", typeof(EditUserRequestDto), """{"email":"a@b.c","roles":["admin","dev"]}""");
        yield return new("hub-update-user-roles", typeof(EditUserRequestDto), """{"email":null,"roles":["dev"]}""");
        yield return new("hub-create-role", typeof(CreateRoleRequestDto), """{"roleName":"surveyor"}""");
        yield return new("hub-change-password", typeof(ChangeUserPasswordRequestDto),
            """{"currentPassword":"old","newPassword":"new"}""");
        yield return new("hub-change-password-admin", typeof(ChangeUserPasswordRequestDto),
            """{"newPassword":"new"}""");
        yield return new("hub-add-user-to-org", typeof(AddUserToOrganizationDto), """{"orgSlug":"acme","permissions":2}""");
        yield return new("hub-update-user-org", typeof(UpdateMemberPermissionsDto), """{"permissions":3}""");
        // organizationsApi.js
        yield return new("hub-add-member", typeof(AddOrganizationMemberDto), """{"userName":"carol","permissions":1}""");
        yield return new("hub-update-member", typeof(UpdateMemberPermissionsDto), """{"permissions":0}""");
        // organization.js (import)
        yield return new("hub-import-verify", typeof(VerifyImportRequestDto),
            """{"sourceType":"registry","params":{"url":"https://hub.example","username":"u","password":"p"}}""");
        yield return new("hub-import-browse", typeof(BrowseImportRequestDto),
            """{"sourceType":"registry","params":{"url":"https://hub.example"},"browseType":"organizations"}""");
        yield return new("hub-import-create", typeof(CreateImportRequestDto),
            """{"sourceType":"registry","params":{"org":"o","ds":"d"},"slug":"imported","name":"Imported","visibility":1}""");
        yield return new("hub-import-create-minimal", typeof(CreateImportRequestDto),
            """{"sourceType":"zip","params":{}}""");
        // dataset.js (tasks, url import)
        yield return new("hub-submit-task", typeof(SubmitTaskRequestDto),
            """{"toolId":"raster-export","path":"ortho.tif","params":{"format":"geotiff","scale":1.5,"bands":[1,2,3]},"force":false}""");
        yield return new("hub-submit-task-no-params", typeof(SubmitTaskRequestDto),
            """{"toolId":"rescan-index","params":{},"force":true}""");
        yield return new("hub-verify-url", typeof(UrlImportVerifyRequestDto),
            """{"url":"https://example.test/a.tif","username":null,"password":null}""");
        yield return new("hub-import-url", typeof(UrlImportRequestDto),
            """{"url":"https://example.test/a.tif","fileName":"a.tif","folder":"sub","overwrite":false,"sizeBytes":123456789}""");
        // dataset.js (raster and stockpile)
        yield return new("hub-merge-multispectral", typeof(MergeMultispectralRequestDto),
            """{"paths":["b1.tif","b2.tif"],"outputPath":"merged.tif"}""");
        yield return new("hub-align-validate", typeof(AlignRasterRequestDto),
            """{"sourcePath":"a.tif","referencePath":"b.tif"}""");
        yield return new("hub-raster-profile", typeof(RasterProfileRequestDto),
            """{"path":"dem.tif","lineString":"{\"type\":\"LineString\",\"coordinates\":[[0,0],[1,1]]}","samples":256}""");
        yield return new("hub-stockpile-calculate", typeof(VolumeCalculationRequestDto),
            """{"path":"dem.tif","polygon":"{\"type\":\"Polygon\",\"coordinates\":[]}","baseMethod":"lowest_perimeter","flatElevation":0}""");
        yield return new("hub-stockpile-detect", typeof(StockpileDetectionRequestDto),
            """{"path":"dem.tif","lat":45.1,"lon":11.2,"radius":50,"sensitivity":0.5}""");
        yield return new("hub-contours", typeof(ContourRequestDto),
            """{"path":"dem.tif","interval":5,"count":null,"baseOffset":0,"minElev":null,"maxElev":null,"simplifyTolerance":0,"bandIndex":1}""");
        yield return new("hub-mask-borders", typeof(MaskBordersRequestDto), """{"path":"ortho.tif","near":15,"white":false}""");
        yield return new("hub-mask-borders-check", typeof(MaskBordersRequestDto), """{"path":"ortho.tif"}""");
        yield return new("hub-stockpile-detect-all", typeof(StockpileBatchDetectionRequestDto),
            """{"path":"dem.tif","sensitivity":0.5,"minAreaM2":5,"maxResults":50}""");
        // users/{user}/meta: a free-form dictionary stored in the user's metadata column (whole numbers are long)
        yield return new("user-meta", typeof(Dictionary<string, object>),
            """{"maxStorage":1073741824,"roles":["a","b"],"ratio":0.25,"whole":2.0,"since":"2026-01-01T00:00:00Z","day":"2026-01-01","nested":{"k":[1,{"x":null}]},"flag":true,"none":null,"text":"x"}""");
        // STAC search (a STAC client posts the spec's body)
        yield return new("stac-search", typeof(StacSearchRequestDto),
            """{"bbox":[11.0,45.0,12.5,46.0],"datetime":"2024-01-01T00:00:00Z/..","limit":10,"collections":["org/ds"],"ids":["a","b"],"token":"next:1","intersects":{"type":"Point","coordinates":[11.5,45.5]}}""");
        // scripts and the admin API
        yield return new("sys-move-datasets", typeof(MoveDatasetDto),
            """{"sourceOrgSlug":"a","datasetSlugs":["x","y"],"destinationOrgSlug":"b","conflictResolution":2}""");
        yield return new("sys-merge-organizations", typeof(MergeOrganizationDto),
            """{"sourceOrgSlug":"a","destinationOrgSlug":"b","conflictResolution":1,"deleteSourceOrganization":false}""");
        yield return new("sys-import-dataset", typeof(ImportDatasetRequestDto),
            """{"sourceRegistryUrl":"https://r.example","username":"u","password":"p","sourceOrganization":"so","sourceDataset":"sd","destinationOrganization":"do","destinationDataset":"dd","mode":1}""");
        yield return new("sys-import-organization", typeof(ImportOrganizationRequestDto),
            """{"sourceRegistryUrl":"https://r.example","username":"u","password":"p","sourceOrganization":"so","destinationOrganization":"do"}""");
        yield return new("sys-cleanup-builds", typeof(CleanupBuildRequestDto), """{"organizationSlug":"o","datasetSlug":"d"}""");
        yield return new("sys-rescan", typeof(RescanRequestDto),
            """{"organizationSlug":"o","datasetSlug":null,"types":["image"],"extensions":[".jpg"],"stopOnError":true}""");
    }

    /// <summary>What the formatter accepts and refuses beyond the Hub's payloads.</summary>
    private static IEnumerable<Case> Leniencies()
    {
        var search = typeof(StacSearchRequestDto);
        var mask = typeof(MaskBordersRequestDto);
        var move = typeof(MoveDatasetDto);
        var user = typeof(CreateUserRequest);
        var member = typeof(AddOrganizationMemberDto);

        // member names
        yield return new("names-pascal-case", user, """{"UserName":"a","Password":"p","Email":"e","Roles":["r"]}""");
        yield return new("names-upper-case", user, """{"USERNAME":"a","PASSWORD":"p"}""");
        yield return new("names-explicit-attribute-case", search, """{"LIMIT":5,"Datetime":"x","COLLECTIONS":["a"]}""");
        yield return new("names-snake-case-is-not-camel", user, """{"user_name":"a","password":"p"}""");
        // numbers
        yield return new("number-in-string-int", search, """{"limit":"5"}""");
        yield return new("number-in-string-double", typeof(StockpileDetectionRequestDto),
            """{"path":"d.tif","lat":"45.5","lon":"11.5","radius":"50"}""");
        yield return new("number-float-for-int", search, """{"limit":5.0}""");
        yield return new("number-fraction-for-int", search, """{"limit":5.5}""");
        yield return new("number-exponent-for-int", mask, """{"path":"a.tif","near":1e1}""");
        yield return new("number-too-large-for-int", mask, """{"path":"a.tif","near":99999999999}""");
        yield return new("number-text-not-a-number", search, """{"limit":"five"}""");
        yield return new("number-empty-string-for-nullable-int", search, """{"limit":""}""");
        yield return new("number-null-for-int", mask, """{"path":"a.tif","near":null}""");
        yield return new("number-negative-ok", search, """{"limit":-1}""");
        // booleans
        yield return new("bool-true-string", mask, """{"path":"a.tif","white":"true"}""");
        yield return new("bool-capitalised-string", mask, """{"path":"a.tif","white":"True"}""");
        yield return new("bool-number", mask, """{"path":"a.tif","white":1}""");
        yield return new("bool-text-not-a-bool", mask, """{"path":"a.tif","white":"yes"}""");
        yield return new("bool-null", mask, """{"path":"a.tif","white":null}""");
        // strings
        yield return new("string-from-number", user, """{"userName":"a","password":12345}""");
        yield return new("string-from-bool", user, """{"userName":"a","password":true}""");
        yield return new("string-from-object", user, """{"userName":{"x":1},"password":"p"}""");
        yield return new("string-array-of-numbers", search, """{"ids":[1,2]}""");
        yield return new("string-unicode-escape", user, """{"userName":"è😀","password":"p"}""");
        yield return new("string-single-quotes", user, "{'userName':'a','password':'p'}");
        // enums
        yield return new("enum-name", move, """{"datasetSlugs":["x"],"destinationOrgSlug":"b","conflictResolution":"Rename"}""");
        yield return new("enum-name-lower-case", move, """{"datasetSlugs":["x"],"destinationOrgSlug":"b","conflictResolution":"rename"}""");
        yield return new("enum-name-upper-case", move, """{"datasetSlugs":["x"],"destinationOrgSlug":"b","conflictResolution":"OVERWRITE"}""");
        yield return new("enum-number", move, """{"datasetSlugs":["x"],"destinationOrgSlug":"b","conflictResolution":2}""");
        yield return new("enum-number-in-string", move, """{"datasetSlugs":["x"],"destinationOrgSlug":"b","conflictResolution":"2"}""");
        yield return new("enum-unknown-number", move, """{"datasetSlugs":["x"],"destinationOrgSlug":"b","conflictResolution":9}""");
        yield return new("enum-unknown-name", move, """{"datasetSlugs":["x"],"destinationOrgSlug":"b","conflictResolution":"Merge"}""");
        yield return new("enum-null", move, """{"datasetSlugs":["x"],"destinationOrgSlug":"b","conflictResolution":null}""");
        yield return new("enum-permissions-name", member, """{"userName":"c","permissions":"Admin"}""");
        yield return new("enum-permissions-number", member, """{"userName":"c","permissions":3}""");
        // members
        yield return new("members-unknown-ignored", mask, """{"path":"a.tif","public":true,"extra":{"a":[1]}}""");
        yield return new("members-duplicate-last-wins", mask, """{"path":"a.tif","near":1,"near":2}""");
        yield return new("members-missing-take-defaults", mask, """{"path":"a.tif"}""");
        yield return new("members-empty-object", mask, "{}");
        // document syntax
        yield return new("syntax-comments", mask, "{\"path\":\"a.tif\", /* c */ \"near\":3 // d\n}");
        yield return new("syntax-trailing-comma", mask, """{"path":"a.tif","near":3,}""");
        yield return new("syntax-trailing-comma-array", user, """{"userName":"a","password":"p","roles":["r",]}""");
        yield return new("syntax-unquoted-names", mask, "{path:\"a.tif\",near:3}");
        yield return new("syntax-not-json", mask, "path=a.tif&near=3");
        yield return new("syntax-truncated", mask, """{"path":"a.tif",""");
        yield return new("syntax-array-for-object", mask, """[{"path":"a.tif"}]""");
        yield return new("syntax-null-body", mask, "null");
        yield return new("syntax-empty-body", mask, "");
        yield return new("syntax-bom", mask, "﻿{\"path\":\"a.tif\"}");
        // arrays and dictionaries
        yield return new("array-single-value-for-array", user, """{"userName":"a","password":"p","roles":"admin"}""");
        yield return new("array-null", user, """{"userName":"a","password":"p","roles":null}""");
        yield return new("array-nested-null-element", search, """{"collections":["a",null]}""");
        yield return new("dictionary-number-value-for-string-map", typeof(VerifyImportRequestDto),
            """{"sourceType":"registry","params":{"port":8080,"tls":true}}""");
        yield return new("dictionary-key-casing-kept", typeof(VerifyImportRequestDto),
            """{"sourceType":"registry","params":{"Url":"u","url":"v"}}""");
        // documents
        yield return new("document-intersects-geometry", search,
            """{"intersects":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""");
        yield return new("document-intersects-number-forms", search, """{"intersects":[1,2.0,3.5,1e2,"2026-01-01T00:00:00Z"]}""");
        yield return new("document-task-params-scalar", typeof(SubmitTaskRequestDto), """{"toolId":"t","params":5}""");
        yield return new("document-task-params-null", typeof(SubmitTaskRequestDto), """{"toolId":"t","params":null}""");
        // dataset paths (the model binder rule applies to JSON bodies too)
        yield return new("path-valid", mask, """{"path":"sub/dir/a.tif"}""");
        yield return new("path-traversal-refused", mask, """{"path":"../a.tif"}""");
        yield return new("path-absolute-refused", mask, """{"path":"/etc/passwd"}""");
        yield return new("path-reserved-refused", mask, """{"path":".ddb/dbase.sqlite"}""");
        yield return new("path-null-required", mask, """{"path":null}""");
        yield return new("path-array-of-nullable", typeof(MergeMultispectralRequestDto), """{"paths":["a.tif",null,"b.tif"]}""");
        yield return new("path-number", mask, """{"path":5}""");
    }

    public static IEnumerable<Case> All() => HubPayloads().Concat(Leniencies());

    [Test]
    public async Task EveryCase_IsBoundAsRecorded()
    {
        var recorded = new JsonObject();
        foreach (var c in All())
        {
            var outcome = new JsonObject { ["type"] = c.Type.Name, ["input"] = c.Json };
            MvcJson.BodyBinding binding;
            try
            {
                binding = await Bind(c);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A formatter that throws instead of recording a model error is a refusal too (MVC answers 400/415 or 500: the
                // snapshot keeps the difference visible)
                outcome["outcome"] = "throws " + ex.GetType().Name;
                recorded[c.Name] = outcome;
                continue;
            }

            outcome["outcome"] = binding.HasError ? "refused" : binding.Model == null ? "no model" : "bound";
            if (!binding.HasError && binding.Model != null)
                outcome["model"] = JsonNode.Parse(await MvcJson.WriteAsync(_factory.Services, binding.Model));
            recorded[c.Name] = outcome;
        }

        var failure = SnapshotFile.VerifyDocument(SnapshotName, recorded.ToJsonString(Indented));
        failure.ShouldBeNull(failure);
    }

    [Test]
    public void Cases_HaveUniqueNames() => All().GroupBy(c => c.Name).Where(g => g.Count() > 1).ShouldBeEmpty();

    [Test]
    public void EveryJsonBodyDto_HasAHubPayload()
    {
        // A new [FromBody] DTO without a recorded payload would be bound by a stack nobody tested
        var bodyTypes = typeof(Program).Assembly.GetExportedTypes()
            .Where(t => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                                          System.Reflection.BindingFlags.DeclaredOnly))
            .SelectMany(m => m.GetParameters())
            .Where(p => p.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.FromBodyAttribute), false).Length > 0 &&
                        p.ParameterType != typeof(string))
            .Select(p => p.ParameterType)
            .Distinct()
            .ToList();

        var covered = HubPayloads().Select(c => c.Type).ToHashSet();
        bodyTypes.Where(t => !covered.Contains(t)).Select(t => t.Name).ShouldBeEmpty();
    }

    private Task<MvcJson.BodyBinding> Bind(Case c) => MvcJson.ReadAsync(_factory.Services, c.Type, c.Json);

}
