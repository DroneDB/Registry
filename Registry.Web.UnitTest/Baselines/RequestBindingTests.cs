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

namespace Registry.Web.Test.Baselines;

/// <summary>
/// How MVC binds the JSON request bodies, pinned before the System.Text.Json switch and unchanged
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
public sealed class RequestBindingTests : IDisposable
{
    private const string SnapshotName = "wire/request-binding.json";

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private RegistryWebFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _factory = new RegistryWebFactory();

    /// <summary>NUnit disposes the fixture after its last test.</summary>
    public void Dispose() => _factory.Dispose();

    public sealed record BindingCase(string Name, Type Type, string Json)
    {
        public override string ToString() => Name;
    }

    /// <summary>The payloads the Hub sends, one per JSON-body DTO.</summary>
    private static IEnumerable<BindingCase> HubPayloads()
    {
        // adminApi.js
        yield return new BindingCase("hub-create-user", typeof(CreateUserRequest),
            """{"userName":"alice","email":"alice@example.com","password":"Secret123!","roles":["admin"]}""");
        yield return new BindingCase("hub-create-user-no-roles", typeof(CreateUserRequest),
            """{"userName":"bob","email":"","password":"Secret123!","roles":[]}""");
        yield return new BindingCase("hub-update-user", typeof(EditUserRequestDto), """{"email":"a@b.c","roles":["admin","dev"]}""");
        yield return new BindingCase("hub-update-user-roles", typeof(EditUserRequestDto), """{"email":null,"roles":["dev"]}""");
        yield return new BindingCase("hub-create-role", typeof(CreateRoleRequestDto), """{"roleName":"surveyor"}""");
        yield return new BindingCase("hub-change-password", typeof(ChangeUserPasswordRequestDto),
            """{"currentPassword":"old","newPassword":"new"}""");
        yield return new BindingCase("hub-change-password-admin", typeof(ChangeUserPasswordRequestDto),
            """{"newPassword":"new"}""");
        yield return new BindingCase("hub-add-user-to-org", typeof(AddUserToOrganizationDto), """{"orgSlug":"acme","permissions":2}""");
        yield return new BindingCase("hub-update-user-org", typeof(UpdateMemberPermissionsDto), """{"permissions":3}""");
        // organizationsApi.js
        yield return new BindingCase("hub-add-member", typeof(AddOrganizationMemberDto), """{"userName":"carol","permissions":1}""");
        yield return new BindingCase("hub-update-member", typeof(UpdateMemberPermissionsDto), """{"permissions":0}""");
        // organization.js (import)
        yield return new BindingCase("hub-import-verify", typeof(VerifyImportRequestDto),
            """{"sourceType":"registry","params":{"url":"https://hub.example","username":"u","password":"p"}}""");
        yield return new BindingCase("hub-import-browse", typeof(BrowseImportRequestDto),
            """{"sourceType":"registry","params":{"url":"https://hub.example"},"browseType":"organizations"}""");
        yield return new BindingCase("hub-import-create", typeof(CreateImportRequestDto),
            """{"sourceType":"registry","params":{"org":"o","ds":"d"},"slug":"imported","name":"Imported","visibility":1}""");
        yield return new BindingCase("hub-import-create-minimal", typeof(CreateImportRequestDto),
            """{"sourceType":"zip","params":{}}""");
        // dataset.js (tasks, url import)
        yield return new BindingCase("hub-submit-task", typeof(SubmitTaskRequestDto),
            """{"toolId":"raster-export","path":"ortho.tif","params":{"format":"geotiff","scale":1.5,"bands":[1,2,3]},"force":false}""");
        yield return new BindingCase("hub-submit-task-no-params", typeof(SubmitTaskRequestDto),
            """{"toolId":"rescan-index","params":{},"force":true}""");
        yield return new BindingCase("hub-verify-url", typeof(UrlImportVerifyRequestDto),
            """{"url":"https://example.test/a.tif","username":null,"password":null}""");
        yield return new BindingCase("hub-import-url", typeof(UrlImportRequestDto),
            """{"url":"https://example.test/a.tif","fileName":"a.tif","folder":"sub","overwrite":false,"sizeBytes":123456789}""");
        // dataset.js (raster and stockpile)
        yield return new BindingCase("hub-merge-multispectral", typeof(MergeMultispectralRequestDto),
            """{"paths":["b1.tif","b2.tif"],"outputPath":"merged.tif"}""");
        yield return new BindingCase("hub-align-validate", typeof(AlignRasterRequestDto),
            """{"sourcePath":"a.tif","referencePath":"b.tif"}""");
        yield return new BindingCase("hub-raster-profile", typeof(RasterProfileRequestDto),
            """{"path":"dem.tif","lineString":"{\"type\":\"LineString\",\"coordinates\":[[0,0],[1,1]]}","samples":256}""");
        yield return new BindingCase("hub-stockpile-calculate", typeof(VolumeCalculationRequestDto),
            """{"path":"dem.tif","polygon":"{\"type\":\"Polygon\",\"coordinates\":[]}","baseMethod":"lowest_perimeter","flatElevation":0}""");
        yield return new BindingCase("hub-stockpile-detect", typeof(StockpileDetectionRequestDto),
            """{"path":"dem.tif","lat":45.1,"lon":11.2,"radius":50,"sensitivity":0.5}""");
        yield return new BindingCase("hub-contours", typeof(ContourRequestDto),
            """{"path":"dem.tif","interval":5,"count":null,"baseOffset":0,"minElev":null,"maxElev":null,"simplifyTolerance":0,"bandIndex":1}""");
        yield return new BindingCase("hub-mask-borders", typeof(MaskBordersRequestDto), """{"path":"ortho.tif","near":15,"white":false}""");
        yield return new BindingCase("hub-mask-borders-check", typeof(MaskBordersRequestDto), """{"path":"ortho.tif"}""");
        yield return new BindingCase("hub-stockpile-detect-all", typeof(StockpileBatchDetectionRequestDto),
            """{"path":"dem.tif","sensitivity":0.5,"minAreaM2":5,"maxResults":50}""");
        // users/{user}/meta: a free-form dictionary stored in the user's metadata column (whole numbers are long)
        yield return new BindingCase("user-meta", typeof(Dictionary<string, object>),
            """{"maxStorage":1073741824,"roles":["a","b"],"ratio":0.25,"whole":2.0,"since":"2026-01-01T00:00:00Z","day":"2026-01-01","nested":{"k":[1,{"x":null}]},"flag":true,"none":null,"text":"x"}""");
        // PATCH users/preferences: sections by name; a null section deletes it (the Hub's preferences store)
        yield return new BindingCase("hub-patch-preferences", typeof(Dictionary<string, JsonElement>),
            """{"units":{"measurement":"metric"},"map":{"zoom":12.5,"layers":["osm","sat"],"center":[11.25,45]},"orgs":null}""");
        // STAC search (a STAC client posts the spec's body)
        yield return new BindingCase("stac-search", typeof(StacSearchRequestDto),
            """{"bbox":[11.0,45.0,12.5,46.0],"datetime":"2024-01-01T00:00:00Z/..","limit":10,"collections":["org/ds"],"ids":["a","b"],"token":"next:1","intersects":{"type":"Point","coordinates":[11.5,45.5]}}""");
        // scripts and the admin API
        yield return new BindingCase("sys-move-datasets", typeof(MoveDatasetDto),
            """{"sourceOrgSlug":"a","datasetSlugs":["x","y"],"destinationOrgSlug":"b","conflictResolution":2}""");
        yield return new BindingCase("sys-merge-organizations", typeof(MergeOrganizationDto),
            """{"sourceOrgSlug":"a","destinationOrgSlug":"b","conflictResolution":1,"deleteSourceOrganization":false}""");
        yield return new BindingCase("sys-import-dataset", typeof(ImportDatasetRequestDto),
            """{"sourceRegistryUrl":"https://r.example","username":"u","password":"p","sourceOrganization":"so","sourceDataset":"sd","destinationOrganization":"do","destinationDataset":"dd","mode":1}""");
        yield return new BindingCase("sys-import-organization", typeof(ImportOrganizationRequestDto),
            """{"sourceRegistryUrl":"https://r.example","username":"u","password":"p","sourceOrganization":"so","destinationOrganization":"do"}""");
        yield return new BindingCase("sys-cleanup-builds", typeof(CleanupBuildRequestDto), """{"organizationSlug":"o","datasetSlug":"d"}""");
        yield return new BindingCase("sys-rescan", typeof(RescanRequestDto),
            """{"organizationSlug":"o","datasetSlug":null,"types":["image"],"extensions":[".jpg"],"stopOnError":true}""");
        yield return new BindingCase("sys-email-test", typeof(TestEmailRequestDto), """{"to":"admin@example.com"}""");
        yield return new BindingCase("sys-update-check-settings", typeof(UpdateCheckSettingsDto),
            """{"useDeclaration":"commercial_needs_license","contactEmail":"it@acme-survey.com","contactConsent":true}""");
        yield return new BindingCase("sys-update-check-settings-cleared", typeof(UpdateCheckSettingsDto),
            """{"useDeclaration":null,"contactEmail":null,"contactConsent":false}""");
    }

    /// <summary>What the formatter accepts and refuses beyond the Hub's payloads.</summary>
    private static IEnumerable<BindingCase> Leniencies()
    {
        var search = typeof(StacSearchRequestDto);
        var mask = typeof(MaskBordersRequestDto);
        var move = typeof(MoveDatasetDto);
        var user = typeof(CreateUserRequest);
        var member = typeof(AddOrganizationMemberDto);

        // member names
        yield return new BindingCase("names-pascal-case", user, """{"UserName":"a","Password":"p","Email":"e","Roles":["r"]}""");
        yield return new BindingCase("names-upper-case", user, """{"USERNAME":"a","PASSWORD":"p"}""");
        yield return new BindingCase("names-explicit-attribute-case", search, """{"LIMIT":5,"Datetime":"x","COLLECTIONS":["a"]}""");
        yield return new BindingCase("names-snake-case-is-not-camel", user, """{"user_name":"a","password":"p"}""");
        // numbers
        yield return new BindingCase("number-in-string-int", search, """{"limit":"5"}""");
        yield return new BindingCase("number-in-string-double", typeof(StockpileDetectionRequestDto),
            """{"path":"d.tif","lat":"45.5","lon":"11.5","radius":"50"}""");
        yield return new BindingCase("number-float-for-int", search, """{"limit":5.0}""");
        yield return new BindingCase("number-fraction-for-int", search, """{"limit":5.5}""");
        yield return new BindingCase("number-exponent-for-int", mask, """{"path":"a.tif","near":1e1}""");
        yield return new BindingCase("number-too-large-for-int", mask, """{"path":"a.tif","near":99999999999}""");
        yield return new BindingCase("number-text-not-a-number", search, """{"limit":"five"}""");
        yield return new BindingCase("number-empty-string-for-nullable-int", search, """{"limit":""}""");
        yield return new BindingCase("number-null-for-int", mask, """{"path":"a.tif","near":null}""");
        yield return new BindingCase("number-negative-ok", search, """{"limit":-1}""");
        // booleans
        yield return new BindingCase("bool-true-string", mask, """{"path":"a.tif","white":"true"}""");
        yield return new BindingCase("bool-capitalised-string", mask, """{"path":"a.tif","white":"True"}""");
        yield return new BindingCase("bool-number", mask, """{"path":"a.tif","white":1}""");
        yield return new BindingCase("bool-text-not-a-bool", mask, """{"path":"a.tif","white":"yes"}""");
        yield return new BindingCase("bool-null", mask, """{"path":"a.tif","white":null}""");
        // strings
        yield return new BindingCase("string-from-number", user, """{"userName":"a","password":12345}""");
        yield return new BindingCase("string-from-bool", user, """{"userName":"a","password":true}""");
        yield return new BindingCase("string-from-object", user, """{"userName":{"x":1},"password":"p"}""");
        yield return new BindingCase("string-array-of-numbers", search, """{"ids":[1,2]}""");
        yield return new BindingCase("string-unicode-escape", user, """{"userName":"è😀","password":"p"}""");
        yield return new BindingCase("string-single-quotes", user, "{'userName':'a','password':'p'}");
        // enums
        yield return new BindingCase("enum-name", move, """{"datasetSlugs":["x"],"destinationOrgSlug":"b","conflictResolution":"Rename"}""");
        yield return new BindingCase("enum-name-lower-case", move, """{"datasetSlugs":["x"],"destinationOrgSlug":"b","conflictResolution":"rename"}""");
        yield return new BindingCase("enum-name-upper-case", move, """{"datasetSlugs":["x"],"destinationOrgSlug":"b","conflictResolution":"OVERWRITE"}""");
        yield return new BindingCase("enum-number", move, """{"datasetSlugs":["x"],"destinationOrgSlug":"b","conflictResolution":2}""");
        yield return new BindingCase("enum-number-in-string", move, """{"datasetSlugs":["x"],"destinationOrgSlug":"b","conflictResolution":"2"}""");
        yield return new BindingCase("enum-unknown-number", move, """{"datasetSlugs":["x"],"destinationOrgSlug":"b","conflictResolution":9}""");
        yield return new BindingCase("enum-unknown-name", move, """{"datasetSlugs":["x"],"destinationOrgSlug":"b","conflictResolution":"Merge"}""");
        yield return new BindingCase("enum-null", move, """{"datasetSlugs":["x"],"destinationOrgSlug":"b","conflictResolution":null}""");
        yield return new BindingCase("enum-permissions-name", member, """{"userName":"c","permissions":"Admin"}""");
        yield return new BindingCase("enum-permissions-number", member, """{"userName":"c","permissions":3}""");
        // members
        yield return new BindingCase("members-unknown-ignored", mask, """{"path":"a.tif","public":true,"extra":{"a":[1]}}""");
        yield return new BindingCase("members-duplicate-last-wins", mask, """{"path":"a.tif","near":1,"near":2}""");
        yield return new BindingCase("members-missing-take-defaults", mask, """{"path":"a.tif"}""");
        yield return new BindingCase("members-empty-object", mask, "{}");
        // document syntax
        yield return new BindingCase("syntax-comments", mask, "{\"path\":\"a.tif\", /* c */ \"near\":3 // d\n}");
        yield return new BindingCase("syntax-trailing-comma", mask, """{"path":"a.tif","near":3,}""");
        yield return new BindingCase("syntax-trailing-comma-array", user, """{"userName":"a","password":"p","roles":["r",]}""");
        yield return new BindingCase("syntax-unquoted-names", mask, "{path:\"a.tif\",near:3}");
        yield return new BindingCase("syntax-not-json", mask, "path=a.tif&near=3");
        yield return new BindingCase("syntax-truncated", mask, """{"path":"a.tif",""");
        yield return new BindingCase("syntax-array-for-object", mask, """[{"path":"a.tif"}]""");
        yield return new BindingCase("syntax-null-body", mask, "null");
        yield return new BindingCase("syntax-empty-body", mask, "");
        yield return new BindingCase("syntax-bom", mask, "﻿{\"path\":\"a.tif\"}");
        // arrays and dictionaries
        yield return new BindingCase("array-single-value-for-array", user, """{"userName":"a","password":"p","roles":"admin"}""");
        yield return new BindingCase("array-null", user, """{"userName":"a","password":"p","roles":null}""");
        yield return new BindingCase("array-nested-null-element", search, """{"collections":["a",null]}""");
        yield return new BindingCase("dictionary-number-value-for-string-map", typeof(VerifyImportRequestDto),
            """{"sourceType":"registry","params":{"port":8080,"tls":true}}""");
        yield return new BindingCase("dictionary-key-casing-kept", typeof(VerifyImportRequestDto),
            """{"sourceType":"registry","params":{"Url":"u","url":"v"}}""");
        // documents
        yield return new BindingCase("document-intersects-geometry", search,
            """{"intersects":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""");
        yield return new BindingCase("document-intersects-number-forms", search, """{"intersects":[1,2.0,3.5,1e2,"2026-01-01T00:00:00Z"]}""");
        yield return new BindingCase("document-task-params-scalar", typeof(SubmitTaskRequestDto), """{"toolId":"t","params":5}""");
        yield return new BindingCase("document-task-params-null", typeof(SubmitTaskRequestDto), """{"toolId":"t","params":null}""");
        // dataset paths (the model binder rule applies to JSON bodies too)
        yield return new BindingCase("path-valid", mask, """{"path":"sub/dir/a.tif"}""");
        yield return new BindingCase("path-traversal-refused", mask, """{"path":"../a.tif"}""");
        yield return new BindingCase("path-absolute-refused", mask, """{"path":"/etc/passwd"}""");
        yield return new BindingCase("path-reserved-refused", mask, """{"path":".ddb/dbase.sqlite"}""");
        yield return new BindingCase("path-null-required", mask, """{"path":null}""");
        yield return new BindingCase("path-array-of-nullable", typeof(MergeMultispectralRequestDto), """{"paths":["a.tif",null,"b.tif"]}""");
        yield return new BindingCase("path-number", mask, """{"path":5}""");
    }

    public static IEnumerable<BindingCase> All() => HubPayloads().Concat(Leniencies());

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

    private Task<MvcJson.BodyBinding> Bind(BindingCase c) => MvcJson.ReadAsync(_factory.Services, c.Type, c.Json);

}
