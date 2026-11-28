using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.OrganizationAssets;
using Registry.Web.Services.Reports;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Serilog.Core;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The report settings of an organization through HTTP on <see cref="RegistryWebFactory"/>: who reads and who writes
/// them, the defaults, the version check, the validation, the logos (types, size, where they are stored, how they are
/// served) and what happens to them with a template and with the organization.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~ReportSettingsHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ReportSettingsHttpTests : IDisposable
{
    private const string Owner = "rs-owner";
    private const string OrgAdmin = "rs-orgadmin";
    private const string Member = "rs-member";
    private const string Outsider = "rs-outsider";
    private const string InstanceAdmin = "rs-instadmin";
    private const string PrivateDs = "private-ds";
    private const string PublicDs = "public-ds";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

    private readonly LogCapture _logs = new();
    private readonly ConcurrentDictionary<string, byte> _publicRefs = new();
    private readonly Dictionary<string, string> _ids = new();
    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private int _orgCounter;
    private Action? _beforeLogoWrite;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory(
            new Dictionary<string, string> { ["Serilog:LevelSwitches:$controlSwitch"] = "Information" },
            services =>
            {
                services.AddSingleton<ILogEventSink>(_logs);
                // The store of the files of the organizations, on a file system that runs the step a test sets just before it writes
                services.AddSingleton(sp => ActivatorUtilities.CreateInstance<OrganizationAssetStore>(sp,
                    SteppedFileSystem.Over(sp.GetRequiredService<IFileSystem>(),
                        () => Interlocked.Exchange(ref _beforeLogoWrite, null))));
            });
        // A dataset is private unless its reference is one of the public ones (native ddb throws on a missing key)
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), "visibility", It.IsAny<string?>()))
            .Returns((string path, string _, string? _) => _publicRefs.Keys.Any(path.Contains)
                ? $$"""{"data":{{(int)Visibility.Public}},"id":"visibility","mtime":0}"""
                : throw new DdbException("Key not found"));
        _factory.DdbWrapper.Setup(d => d.Info(It.IsAny<string>(), false, 0, false))
            .Returns((string path, bool _, int _, bool _) =>
            [
                new Registry.Ports.DroneDB.Entry
                {
                    Path = "file://" + path, Type = EntryType.DroneDB,
                    Properties = TestJson.Props(("entries", 0), ("meta", TestJson.Parse(
                        """{"name":{"data":"Site"},"reports":[{"id":"r_k3x9q2m7","data":{"name":"North pit"}}],"views":[{"id":"v","data":{}}]}""")))
                }
            ]);
        _factory.DdbWrapper.Setup(d => d.ListEntrySummaries(It.IsAny<string>(), It.IsAny<EntryQuery>())).Returns([]);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var name in new[] { Owner, OrgAdmin, Member, Outsider })
            _ids[name] = (await _factory.CreateUserAsync(name)).Id;
        _ids[InstanceAdmin] = (await _factory.CreateUserAsync(InstanceAdmin, isAdmin: true)).Id;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        _logs.Clear();
        _beforeLogoWrite = null;
    }

    #region Fixtures

    /// <summary>A private organization of its own (so no test depends on another): an owner, an administrator, a member and two datasets.</summary>
    private async Task<string> NewOrganizationAsync(bool isPublic = false)
    {
        var slug = $"rs-org-{++_orgCounter}";
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, slug, ownerId: _ids[Owner], isPublic: isPublic);
        Seed.Dataset(context, org, PrivateDs);
        var publicDs = Seed.Dataset(context, org, PublicDs);
        _publicRefs[publicDs.InternalRef.ToString()] = 0;
        foreach (var (user, permission) in new[] { (OrgAdmin, OrganizationPermissions.Admin), (Member, OrganizationPermissions.ReadWrite) })
            context.OrganizationsUsers.Add(new OrganizationUser
            {
                Organization = org, OrganizationSlug = slug, UserId = _ids[user], Permissions = permission,
                GrantedAt = DateTime.UtcNow, GrantedBy = _ids[Owner]
            });
        await context.SaveChangesAsync();
        return slug;
    }

    private sealed record Reply(int Status, HttpResponseMessage Response, byte[] Bytes)
    {
        public string Text => Encoding.UTF8.GetString(Bytes);

        public JsonNode Json => JsonNode.Parse(Text)!;

        public string? Header(string name) =>
            Response.Headers.TryGetValues(name, out var values) || Response.Content.Headers.TryGetValues(name, out values)
                ? string.Join(",", values)
                : null;
    }

    private async Task<Reply> SendAsync(HttpMethod method, string url, string? user, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (user != null)
            request.AsUser(user, isAdmin: user == InstanceAdmin);
        var response = await _client.SendAsync(request);
        return new Reply((int)response.StatusCode, response, await response.Content.ReadAsByteArrayAsync());
    }

    private static string Settings(string org) => $"/orgs/{org}/report-settings";
    private static string Logo(string org, string template) => $"{Settings(org)}/templates/{template}/logo";
    private static string DatasetSettings(string org, string ds) => $"/orgs/{org}/ds/{ds}/report-settings";

    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static JsonObject Update(int? version, params string[] templateIds) => new()
    {
        ["version"] = version,
        ["templates"] = new JsonArray(templateIds.Select(id => JsonNode.Parse(ApiJson.Serialize(Template(id)))).ToArray()),
        ["schema"] = JsonNode.Parse(ApiJson.Serialize(ReportSettingsDefaults.Schema()))
    };

    private static ReportTemplateDto Template(string id)
    {
        var template = ReportSettingsDefaults.StandardTemplate();
        template.Id = id;
        template.Name = id.ToUpperInvariant();
        return template;
    }

    private Task<Reply> PutSettings(string org, JsonObject body, string? user = Owner) =>
        SendAsync(HttpMethod.Put, Settings(org), user, Json(body));

    private Task<Reply> GetSettings(string org, string? user = Owner) => SendAsync(HttpMethod.Get, Settings(org), user);

    private Task<Reply> PutLogo(string org, string template, byte[] bytes, string? user = Owner, string field = "file",
        string fileName = "logo.png")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, field, fileName);
        return SendAsync(HttpMethod.Put, Logo(org, template), user, form);
    }

    private string LogoFolder(string org) => Path.Combine(_factory.StoragePath, "org-assets", org);

    private static string? Stored(Reply settings, string template, string member) =>
        settings.Json["templates"]!.AsArray().Single(t => t!["id"]!.GetValue<string>() == template)![member]?.ToString();

    /// <summary>
    /// A file system that runs a step of the test (once, when the test has set one) just before the next file is
    /// written, and otherwise is the one it wraps: a request can be held at the point where it writes, with the
    /// settings it read, while another runs whole.
    /// </summary>
    [SuppressMessage("Performance", "CA1852", Justification = "DispatchProxy.Create refuses a sealed proxy type")]
    private class SteppedFileSystem : DispatchProxy
    {
        private IFileSystem _inner = null!;
        private Func<Action?> _takeStep = null!;

        public static IFileSystem Over(IFileSystem inner, Func<Action?> takeStep)
        {
            var proxy = Create<IFileSystem, SteppedFileSystem>();
            var stepped = (SteppedFileSystem)(object)proxy;
            stepped._inner = inner;
            stepped._takeStep = takeStep;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name is nameof(IFileSystem.WriteAllBytes) or nameof(IFileSystem.WriteAllBytesDurable))
                _takeStep()?.Invoke();

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }

    #endregion

    #region Reading

    [Test]
    public async Task AnOrganizationWithoutSettings_HasTheDefaultSchemaAndTheStandardTemplate()
    {
        var org = await NewOrganizationAsync();

        var reply = await GetSettings(org, Member);

        reply.Status.ShouldBe(200);
        reply.Json["version"]!.GetValue<int>().ShouldBe(0);
        reply.Json["templates"]!.AsArray().Count.ShouldBe(1);
        var standard = reply.Json["templates"]![0]!;
        standard["id"]!.GetValue<string>().ShouldBe("standard");
        standard["name"]!.GetValue<string>().ShouldBe("Standard");
        standard["logo"].ShouldBeNull();
        reply.Json["schema"]!["categories"]!.AsArray().Count.ShouldBe(5);
        reply.Json["schema"]!["severities"]!.AsArray().Count.ShouldBe(4);
        reply.Json["schema"]!["statuses"]!.AsArray().Select(s => s!["id"]!.GetValue<string>())
            .ShouldBe(["open", "in-progress", "resolved"]);
        reply.Json["schema"]!["statuses"]![2]!["closed"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Test]
    public async Task ATemplateStoredBeforeTheSummaryChart_IsReadWithBars_AndAChosenChartIsStored()
    {
        var org = await NewOrganizationAsync();
        (await PutSettings(org, Update(0, "standard", "brand"))).Status.ShouldBe(200);
        using (var scope = _factory.Services.CreateScope())
        {
            // The row as a Registry that did not know the member wrote it
            var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
            var row = await context.OrganizationReportSettings.SingleAsync(s => s.OrganizationSlug == org);
            var templates = JsonNode.Parse(row.Templates)!.AsArray();
            foreach (var template in templates)
                template!.AsObject().Remove("breakdownChart");
            row.Templates = templates.ToJsonString();
            await context.SaveChangesAsync();
        }

        var read = await GetSettings(org, Member);
        Stored(read, "standard", "breakdownChart").ShouldBe("bars");
        Stored(read, "brand", "breakdownChart").ShouldBe("bars");
        var forReaders = await SendAsync(HttpMethod.Get, DatasetSettings(org, PrivateDs), Member);
        forReaders.Json["templates"]!.AsArray().Select(t => t!["breakdownChart"]!.GetValue<string>()).ShouldBe(["bars", "bars"]);

        var update = Update(1, "standard", "brand");
        update["templates"]![1]!["breakdownChart"] = "donuts";
        (await PutSettings(org, update)).Status.ShouldBe(200);
        var stored = await GetSettings(org, Member);
        Stored(stored, "standard", "breakdownChart").ShouldBe("bars");
        Stored(stored, "brand", "breakdownChart").ShouldBe("donuts");
    }

    [Test]
    public async Task TheDatasetScopedRead_HasTheSameDefaults_AndNoVersion()
    {
        var org = await NewOrganizationAsync();

        var reply = await SendAsync(HttpMethod.Get, DatasetSettings(org, PrivateDs), Owner);

        reply.Status.ShouldBe(200);
        reply.Json["templates"]![0]!["id"]!.GetValue<string>().ShouldBe("standard");
        reply.Json["schema"]!["categories"]!.AsArray().Count.ShouldBe(5);
        reply.Json.AsObject().ContainsKey("version").ShouldBeFalse();
    }

    [Test]
    public async Task TheOrganizationRead_IsForItsMembersAndTheAdministratorsOfTheInstance()
    {
        var org = await NewOrganizationAsync();

        foreach (var user in new[] { Owner, OrgAdmin, Member, InstanceAdmin })
            (await GetSettings(org, user)).Status.ShouldBe(200, user);
        (await GetSettings(org, Outsider)).Status.ShouldBe(403);
        (await GetSettings(org, null)).Status.ShouldBe(401);
        (await GetSettings("rs-no-such-org")).Status.ShouldBe(404);
    }

    [Test]
    public async Task AReaderOfAPublicOrganization_WhoIsNotAMember_IsStillRefused()
    {
        var org = await NewOrganizationAsync(isPublic: true);

        (await GetSettings(org, Outsider)).Status.ShouldBe(403);
    }

    [Test]
    public async Task TheDatasetScopedRead_FollowsTheReadAccessToTheDataset()
    {
        var org = await NewOrganizationAsync();

        (await SendAsync(HttpMethod.Get, DatasetSettings(org, PrivateDs), Member)).Status.ShouldBe(200);
        (await SendAsync(HttpMethod.Get, DatasetSettings(org, PrivateDs), Outsider)).Status.ShouldBe(403);
        (await SendAsync(HttpMethod.Get, DatasetSettings(org, PrivateDs), null)).Status.ShouldBe(401);
        (await SendAsync(HttpMethod.Get, DatasetSettings(org, "no-such-ds"), Owner)).Status.ShouldBe(404);
        // A public dataset of a private organization: the templates are readable by anyone who can read it
        (await SendAsync(HttpMethod.Get, DatasetSettings(org, PublicDs), null)).Status.ShouldBe(200);
        (await SendAsync(HttpMethod.Get, DatasetSettings(org, PublicDs), Outsider)).Status.ShouldBe(200);
    }

    #endregion

    #region Writing

    [Test]
    public async Task TheAdministratorsOfTheOrganization_Write_AndEachWriteIsTheNextVersion()
    {
        var org = await NewOrganizationAsync();
        var version = 0;

        foreach (var user in new[] { Owner, OrgAdmin, InstanceAdmin })
        {
            var reply = await PutSettings(org, Update(version, "standard", "brand"), user);

            reply.Status.ShouldBe(200, $"{user}: {reply.Text}");
            reply.Json["version"]!.GetValue<int>().ShouldBe(++version);
        }

        var stored = await GetSettings(org, Member);
        stored.Json["version"]!.GetValue<int>().ShouldBe(3);
        stored.Json["templates"]!.AsArray().Select(t => t!["id"]!.GetValue<string>()).ShouldBe(["standard", "brand"]);
        Stored(stored, "brand", "name").ShouldBe("BRAND");
    }

    [Test]
    public async Task AMemberWhoIsNotAnAdministrator_CannotWrite()
    {
        var org = await NewOrganizationAsync();

        (await PutSettings(org, Update(0, "standard"), Member)).Status.ShouldBe(403);
        (await PutSettings(org, Update(0, "standard"), Outsider)).Status.ShouldBe(403);
        (await PutSettings(org, Update(0, "standard"), null)).Status.ShouldBe(401);
        (await PutSettings("rs-no-such-org", Update(0, "standard"))).Status.ShouldBe(404);

        (await GetSettings(org)).Json["version"]!.GetValue<int>().ShouldBe(0);
    }

    [Test]
    public async Task AWriteThatIsNotAdministered_IsRefusedBeforeItIsValidated()
    {
        var org = await NewOrganizationAsync();

        (await PutSettings(org, new JsonObject(), Member)).Status.ShouldBe(403);
    }

    [Test]
    public async Task AWriteOfAStaleVersion_IsRefusedWithConflict_AndChangesNothing()
    {
        var org = await NewOrganizationAsync();
        (await PutSettings(org, Update(0, "standard"))).Status.ShouldBe(200);

        var stale = await PutSettings(org, Update(0, "standard", "other"), OrgAdmin);

        stale.Status.ShouldBe(409);
        (await GetSettings(org)).Json["templates"]!.AsArray().Count.ShouldBe(1);
        (await PutSettings(org, Update(5, "standard"))).Status.ShouldBe(409);
    }

    [Test]
    public async Task AWriteWithoutAVersion_IsRefused_NamingIt()
    {
        var org = await NewOrganizationAsync();

        var reply = await PutSettings(org, Update(null, "standard"));

        reply.Status.ShouldBe(400);
        reply.Json["errors"]!.AsObject().Select(e => e.Key).ShouldContain(k => string.Equals(k, "version", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task TwoFirstWritesAtOnce_AreOneWriteAndOneConflict()
    {
        var org = await NewOrganizationAsync();

        var replies = await Task.WhenAll(
            PutSettings(org, Update(0, "standard", "one"), Owner),
            PutSettings(org, Update(0, "standard", "two"), OrgAdmin));

        replies.Select(r => r.Status).Order().ShouldBe([200, 409]);
        (await GetSettings(org)).Json["version"]!.GetValue<int>().ShouldBe(1);
    }

    [Test]
    public async Task ARefusedWrite_Is400WithTheFieldPath_AndStoresNothing()
    {
        var org = await NewOrganizationAsync();
        var body = Update(0, "standard");
        body["templates"]![0]!["name"] = "";
        body["schema"]!["categories"]![1]!["colour"] = "red";

        var reply = await PutSettings(org, body);

        reply.Status.ShouldBe(400);
        // The first refusal only; each refusal names one member
        reply.Json["errors"]!.AsObject().Single().Key.ShouldBe("templates[0].name", StringCompareShould.IgnoreCase);

        body["templates"]![0]!["name"] = "Fine";
        var second = await PutSettings(org, body);
        second.Status.ShouldBe(400);
        second.Json["errors"]!.AsObject().Single().Key.ShouldBe("categories[1].colour", StringCompareShould.IgnoreCase);
        (await GetSettings(org)).Json["version"]!.GetValue<int>().ShouldBe(0);
    }

    [Test]
    public async Task ATemplateIdThatIsNotAnId_IsRefused()
    {
        var org = await NewOrganizationAsync();

        foreach (var id in new[] { "Brand", "bra nd", "../x", "a_b", new string('a', 33) })
            (await PutSettings(org, Update(0, id))).Status.ShouldBe(400, id);
        (await PutSettings(org, Update(0, "ok"))).Status.ShouldBe(200);
    }

    [Test]
    public async Task ASettingsBodyOfTheWrongShape_Is400()
    {
        var org = await NewOrganizationAsync();

        (await PutSettings(org, new JsonObject { ["version"] = 0 })).Status.ShouldBe(400);
        (await SendAsync(HttpMethod.Put, Settings(org), Owner, new StringContent("not json", Encoding.UTF8, "application/json")))
            .Status.ShouldBe(400);
        (await SendAsync(HttpMethod.Put, Settings(org), Owner, new StringContent("null", Encoding.UTF8, "application/json")))
            .Status.ShouldBe(400);
    }

    [Test]
    public async Task ASettingsBodyOverTheLimit_Is413()
    {
        var org = await NewOrganizationAsync();
        var body = Update(0, "standard");
        body["templates"]![0]!["disclaimer"] = new string('d', 300 * 1024);

        (await PutSettings(org, body)).Status.ShouldBe(413);
    }

    [Test]
    public async Task ALogoSentWithTheTemplates_IsIgnored()
    {
        var org = await NewOrganizationAsync();
        var body = Update(0, "standard");
        body["templates"]![0]!["logo"] = "report-logo-standard.png";

        var reply = await PutSettings(org, body);

        reply.Status.ShouldBe(200);
        reply.Json["templates"]![0]!["logo"].ShouldBeNull();
        (await SendAsync(HttpMethod.Get, Logo(org, "standard"), Owner)).Status.ShouldBe(404);
    }

    [Test]
    public async Task AWrite_IsLogged_WithTheOrganizationTheUserAndTheVersion()
    {
        var org = await NewOrganizationAsync();

        (await PutSettings(org, Update(0, "standard"), OrgAdmin)).Status.ShouldBe(200);
        (await PutSettings(org, Update(1, "standard", "brand"), Owner)).Status.ShouldBe(200);

        var lines = _logs.Records.Where(r => r.Category.EndsWith(nameof(ReportSettingsManager), StringComparison.Ordinal) && r.Message.Contains(org)).ToList();
        lines.Count.ShouldBe(2);
        lines[0].Message.ShouldContain($"organization \"{org}\" updated by \"{OrgAdmin}\" to version 1");
        lines[1].Message.ShouldContain($"organization \"{org}\" updated by \"{Owner}\" to version 2");
    }

    #endregion

    #region Logos

    [Test]
    public async Task TheFirstLogo_StoresTheStandardTemplateAsAnOrdinaryOne()
    {
        var org = await NewOrganizationAsync();

        var reply = await PutLogo(org, "standard", Png);

        reply.Status.ShouldBe(200, reply.Text);
        var logo = reply.Json["logo"]!.GetValue<string>();
        logo.ShouldBeLogoOf("standard", "png");
        reply.Json["version"]!.GetValue<int>().ShouldBe(1);
        var stored = await GetSettings(org);
        stored.Json["version"]!.GetValue<int>().ShouldBe(1);
        Stored(stored, "standard", "logo").ShouldBe(logo);
        stored.Json["schema"]!["categories"]!.AsArray().Count.ShouldBe(5);
        File.ReadAllBytes(Path.Combine(LogoFolder(org), logo)).ShouldBe(Png);
        (await PutSettings(org, Update(1, "standard", "second"))).Status.ShouldBe(200);
    }

    [Test]
    public async Task ALogoForATemplateThatIsOnlyInTheDialog_Is404_AndOnceStoredItDoesNotBreakTheNextWrite()
    {
        var org = await NewOrganizationAsync();
        (await PutLogo(org, "brand", Png)).Status.ShouldBe(404);
        Directory.Exists(LogoFolder(org)).ShouldBeFalse();
        (await GetSettings(org)).Json["version"]!.GetValue<int>().ShouldBe(0);

        (await PutSettings(org, Update(0, "standard", "brand"))).Status.ShouldBe(200);
        var logo = await PutLogo(org, "brand", Png);
        logo.Status.ShouldBe(200);
        var version = logo.Json["version"]!.GetValue<int>();
        version.ShouldBe(2);

        var save = await PutSettings(org, Update(version, "standard", "brand"));

        save.Status.ShouldBe(200);
        save.Json["templates"]!.AsArray().Single(t => t!["id"]!.GetValue<string>() == "brand")!["logo"]!.GetValue<string>()
            .ShouldBe(logo.Json["logo"]!.GetValue<string>());
        (await PutSettings(org, Update(1, "standard", "brand"))).Status.ShouldBe(409);
    }

    [Test]
    public async Task ALogoForATemplateThatIsNotStored_Is404WhateverTheRoute()
    {
        var org = await NewOrganizationAsync();
        (await PutSettings(org, Update(0, "standard"))).Status.ShouldBe(200);

        (await PutLogo(org, "brand", Png)).Status.ShouldBe(404);
        (await SendAsync(HttpMethod.Delete, Logo(org, "brand"), Owner)).Status.ShouldBe(404);
        (await SendAsync(HttpMethod.Get, Logo(org, "brand"), Owner)).Status.ShouldBe(404);
        (await SendAsync(HttpMethod.Get, $"/orgs/{org}/ds/{PrivateDs}/report-settings/logo/brand", Owner)).Status.ShouldBe(404);
        (await PutLogo(org, "..%2f..%2fx", Png)).Status.ShouldBe(404);
        (await PutLogo(org, "Bad_Id", Png)).Status.ShouldBe(404);
        Directory.Exists(LogoFolder(org)).ShouldBeFalse();
    }

    [Test]
    public async Task OnlyTheAdministrators_SetAndRemoveALogo()
    {
        var org = await NewOrganizationAsync();

        (await PutLogo(org, "standard", Png, Member)).Status.ShouldBe(403);
        (await PutLogo(org, "standard", Png, Outsider)).Status.ShouldBe(403);
        (await PutLogo(org, "standard", Png, null)).Status.ShouldBe(401);
        (await SendAsync(HttpMethod.Delete, Logo(org, "standard"), Member)).Status.ShouldBe(403);
        (await SendAsync(HttpMethod.Delete, Logo(org, "standard"), null)).Status.ShouldBe(401);
        (await PutLogo(org, "standard", Png, OrgAdmin)).Status.ShouldBe(200);
    }

    [Test]
    public async Task TheTypeOfALogo_IsTheContents()
    {
        var org = await NewOrganizationAsync();

        // A PNG under a .svg name and a JPEG under a PNG content type are what their bytes say
        var asSvgName = await PutLogo(org, "standard", Png, fileName: "logo.svg");
        asSvgName.Json["logo"]!.GetValue<string>().ShouldBeLogoOf("standard", "png");
        var jpeg = await PutLogo(org, "standard", Jpeg);
        jpeg.Json["logo"]!.GetValue<string>().ShouldBeLogoOf("standard", "jpg");

        var webp = await PutLogo(org, "standard", [.. "RIFF"u8, 4, 0, 0, 0, .. "WEBP"u8, 1]);
        var logo = webp.Json["logo"]!.GetValue<string>();
        logo.ShouldBeLogoOf("standard", "webp");
        // One logo per template: the file before is gone
        Directory.GetFiles(LogoFolder(org)).Select(Path.GetFileName).ShouldBe([logo]);
    }

    [Test]
    public async Task WhatIsNotAnAllowedLogo_Is400_AndNothingIsStored()
    {
        var org = await NewOrganizationAsync();

        foreach (var bytes in new[]
                 {
                     Encoding.UTF8.GetBytes("GIF89a....."),
                     Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"),
                     [],
                     [.. Png, .. new byte[ReportLogoStore.MaxBytes]]
                 })
            (await PutLogo(org, "standard", bytes)).Status.ShouldBe(400);

        var noFile = await SendAsync(HttpMethod.Put, Logo(org, "standard"), Owner, new MultipartFormDataContent());
        noFile.Status.ShouldBe(400);
        (await PutLogo(org, "standard", Png, field: "image")).Status.ShouldBe(400);

        Directory.Exists(LogoFolder(org)).ShouldBeFalse();
        (await GetSettings(org)).Json["version"]!.GetValue<int>().ShouldBe(0);
    }

    [Test]
    public async Task ALogoOfExactlyTheLimit_IsAccepted_AndOneFarOverIs413()
    {
        var org = await NewOrganizationAsync();

        (await PutLogo(org, "standard", [.. Png, .. new byte[ReportLogoStore.MaxBytes - Png.Length]])).Status.ShouldBe(200);
        (await PutLogo(org, "standard", [.. Png, .. new byte[2 * 1024 * 1024]])).Status.ShouldBe(413);
    }

    [Test]
    public async Task AnSvgLogo_IsStoredAndServedWithoutScripts()
    {
        var org = await NewOrganizationAsync();
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10" onload="alert(1)">
              <script>alert(2)</script>
              <image href="https://evil.test/x.png"/>
              <rect width="10" height="10" fill="#0070e0"/>
            </svg>
            """;

        var reply = await PutLogo(org, "standard", Encoding.UTF8.GetBytes(svg), fileName: "logo.png");

        var logo = reply.Json["logo"]!.GetValue<string>();
        logo.ShouldBeLogoOf("standard", "svg");
        var served = await SendAsync(HttpMethod.Get, Logo(org, "standard"), Member);
        served.Status.ShouldBe(200);
        served.Header("Content-Type").ShouldStartWith("image/svg+xml");
        served.Text.ShouldContain("<rect");
        served.Text.ShouldNotContain("script");
        served.Text.ShouldNotContain("alert");
        served.Text.ShouldNotContain("evil.test");
        File.ReadAllText(Path.Combine(LogoFolder(org), logo)).ShouldBe(served.Text);
    }

    [Test]
    public async Task ALogo_IsServedWithItsCheckedType_AndNoSniffing()
    {
        var org = await NewOrganizationAsync();
        await PutLogo(org, "standard", Png);

        foreach (var url in new[] { Logo(org, "standard"), $"/orgs/{org}/ds/{PrivateDs}/report-settings/logo/standard" })
        {
            var served = await SendAsync(HttpMethod.Get, url, Member);

            served.Status.ShouldBe(200, url);
            served.Bytes.ShouldBe(Png);
            served.Header("Content-Type").ShouldBe("image/png");
            served.Header("X-Content-Type-Options").ShouldBe("nosniff");
            served.Header("Content-Security-Policy").ShouldBe("sandbox");
            served.Header("Cache-Control").ShouldNotBeNull().ShouldContain("no-cache");
            served.Header("ETag").ShouldNotBeNull();
        }
    }

    [Test]
    public async Task TheLogoOfAPrivateOrganization_IsNotForAnonymousUsersOrStrangers_AndIsNotInTheBrandingFolder()
    {
        var org = await NewOrganizationAsync();
        var logo = (await PutLogo(org, "standard", Png)).Json["logo"]!.GetValue<string>();

        (await SendAsync(HttpMethod.Get, Logo(org, "standard"), null)).Status.ShouldBe(401);
        (await SendAsync(HttpMethod.Get, Logo(org, "standard"), Outsider)).Status.ShouldBe(403);
        (await SendAsync(HttpMethod.Get, $"/orgs/{org}/ds/{PrivateDs}/report-settings/logo/standard", null)).Status.ShouldBe(401);
        (await SendAsync(HttpMethod.Get, $"/orgs/{org}/ds/{PrivateDs}/report-settings/logo/standard", Outsider)).Status.ShouldBe(403);

        foreach (var path in new[]
                 {
                     $"/branding/{logo}", $"/branding/org-assets/{org}/{logo}",
                     $"/branding/../org-assets/{org}/{logo}"
                 })
        {
            var reply = await SendAsync(HttpMethod.Get, path, null);
            reply.Bytes.ShouldNotBe(Png, path);
            reply.Header("Content-Type").ShouldNotStartWith("image/", Case.Insensitive, path);
        }

        Directory.GetFiles(Path.Combine(_factory.StoragePath, "branding"), "*", SearchOption.AllDirectories)
            .ShouldBeEmpty("nothing of the organization is in the public folder");
    }

    [Test]
    public async Task APublicDataset_OfAPrivateOrganization_ServesTheLogoToAnyoneWhoCanReadIt()
    {
        var org = await NewOrganizationAsync();
        await PutLogo(org, "standard", Png);

        var anonymous = await SendAsync(HttpMethod.Get, $"/orgs/{org}/ds/{PublicDs}/report-settings/logo/standard", null);

        anonymous.Status.ShouldBe(200);
        anonymous.Bytes.ShouldBe(Png);
        (await SendAsync(HttpMethod.Get, Logo(org, "standard"), null)).Status.ShouldBe(401);
    }

    [Test]
    public async Task TheLogoOfTheOrganization_IsNamedByTheSettings_AndServedToTheReadersOfADataset()
    {
        var org = await NewOrganizationAsync();
        var dataset = DatasetSettings(org, PublicDs);
        (await GetSettings(org)).Json["organizationLogoUrl"].ShouldBeNull();
        (await SendAsync(HttpMethod.Get, dataset, null)).Json["organizationLogoUrl"].ShouldBeNull();
        (await SendAsync(HttpMethod.Get, $"/orgs/{org}/ds/{PublicDs}/organization-logo", null)).Status.ShouldBe(404);

        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Png), "file", "logo.png");
        var orgLogo = (await SendAsync(HttpMethod.Put, $"/orgs/{org}/logo", Owner, form)).Json["logoUrl"]!.GetValue<string>();

        // A member reads it where the organization is read; a reader of a dataset, through the dataset
        (await GetSettings(org)).Json["organizationLogoUrl"]!.GetValue<string>().ShouldBe(orgLogo);
        var forDataset = (await SendAsync(HttpMethod.Get, dataset, null)).Json["organizationLogoUrl"]!.GetValue<string>();
        forDataset.ShouldBe($"/orgs/{org}/ds/{PublicDs}/organization-logo?v={orgLogo.Split("?v=")[1]}");

        // The organization is private: an anonymous reader of its public dataset gets the logo there, not where the organization is
        var anonymous = await SendAsync(HttpMethod.Get, forDataset, null);
        anonymous.Status.ShouldBe(200);
        anonymous.Bytes.ShouldBe(Png);
        anonymous.Header("Content-Security-Policy").ShouldBe("sandbox");
        anonymous.Header("Cache-Control").ShouldNotBeNull().ShouldContain("immutable");
        (await SendAsync(HttpMethod.Get, orgLogo, null)).Status.ShouldBe(401);

        // A private dataset is not a way to it
        (await SendAsync(HttpMethod.Get, $"/orgs/{org}/ds/{PrivateDs}/organization-logo", null)).Status.ShouldBe(401);
        (await SendAsync(HttpMethod.Get, $"/orgs/{org}/ds/{PrivateDs}/organization-logo", Outsider)).Status.ShouldBe(403);
        (await SendAsync(HttpMethod.Get, $"/orgs/{org}/ds/{PrivateDs}/organization-logo", Member)).Status.ShouldBe(200);
    }

    [TestCase(null)]
    [TestCase("png")]
    [TestCase("jpg")]
    public async Task TwoLogosAtOnce_TheOneThatLoses_LeavesTheWinnersFileAsItWasWritten(string? before)
    {
        var org = await NewOrganizationAsync();
        if (before != null)
            (await PutLogo(org, "standard", before == "png" ? Png : Jpeg)).Status.ShouldBe(200);
        byte[] winnerBytes = [.. Png, 7];
        byte[] loserBytes = [.. Png, 8];
        Reply? winner = null;
        // The loser has read the settings and is about to write its file when the winner sets the logo and saves
        _beforeLogoWrite = () => winner = PutLogo(org, "standard", winnerBytes, OrgAdmin).GetAwaiter().GetResult();

        var loser = await PutLogo(org, "standard", loserBytes);

        winner.ShouldNotBeNull().Status.ShouldBe(200, winner.Text);
        loser.Status.ShouldBe(409, loser.Text);
        var logo = winner.Json["logo"]!.GetValue<string>();
        Stored(await GetSettings(org), "standard", "logo").ShouldBe(logo);
        var served = await SendAsync(HttpMethod.Get, Logo(org, "standard"), Owner);
        served.Status.ShouldBe(200);
        served.Bytes.ShouldBe(winnerBytes);
        // Neither the loser's file nor the logo before stays behind
        Directory.GetFiles(LogoFolder(org)).Select(Path.GetFileName).ShouldBe([logo]);
    }

    [Test]
    public async Task RemovingALogo_IsTheNextVersion_AndDeletesTheFile()
    {
        var org = await NewOrganizationAsync();
        var logo = (await PutLogo(org, "standard", Png)).Json["logo"]!.GetValue<string>();

        var removed = await SendAsync(HttpMethod.Delete, Logo(org, "standard"), OrgAdmin);

        removed.Status.ShouldBe(200);
        removed.Json["version"]!.GetValue<int>().ShouldBe(2);
        Stored(await GetSettings(org), "standard", "logo").ShouldBeNull();
        (await SendAsync(HttpMethod.Get, Logo(org, "standard"), Owner)).Status.ShouldBe(404);
        File.Exists(Path.Combine(LogoFolder(org), logo)).ShouldBeFalse();
        (await PutSettings(org, Update(2, "standard"))).Status.ShouldBe(200);
    }

    [Test]
    public async Task RemovingTheLogoOfATemplateWithoutOne_ChangesNothing()
    {
        var org = await NewOrganizationAsync();
        (await PutSettings(org, Update(0, "standard"))).Status.ShouldBe(200);

        var removed = await SendAsync(HttpMethod.Delete, Logo(org, "standard"), Owner);

        removed.Status.ShouldBe(200);
        removed.Json["version"]!.GetValue<int>().ShouldBe(1);
    }

    [Test]
    public async Task ATemplateThatLeavesTheList_TakesItsLogoFileWithIt_AndAReusedIdStartsWithoutOne()
    {
        var org = await NewOrganizationAsync();
        (await PutSettings(org, Update(0, "standard", "brand", "other"))).Status.ShouldBe(200);
        await PutLogo(org, "brand", Png);
        var otherLogo = (await PutLogo(org, "other", Jpeg)).Json["logo"]!.GetValue<string>();
        Directory.GetFiles(LogoFolder(org)).Length.ShouldBe(2);

        var reply = await PutSettings(org, Update(3, "standard", "other"));

        reply.Status.ShouldBe(200);
        Directory.GetFiles(LogoFolder(org)).Select(Path.GetFileName).ShouldBe([otherLogo]);
        Stored(reply, "other", "logo").ShouldBe(otherLogo);

        var again = await PutSettings(org, Update(4, "standard", "other", "brand"));

        Stored(again, "brand", "logo").ShouldBeNull();
        (await SendAsync(HttpMethod.Get, Logo(org, "brand"), Owner)).Status.ShouldBe(404);
    }

    [Test]
    public async Task DeletingTheOrganization_DeletesItsSettingsAndItsLogos()
    {
        var org = await NewOrganizationAsync();
        await PutLogo(org, "standard", Png);
        Directory.Exists(LogoFolder(org)).ShouldBeTrue();

        // The datasets of the organization are deleted first by the delete; only its settings and files are checked here
        var deleted = await SendAsync(HttpMethod.Delete, $"/orgs/{org}", Owner);

        deleted.Status.ShouldBe(204, deleted.Text);
        Directory.Exists(LogoFolder(org)).ShouldBeFalse();
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<RegistryContext>().OrganizationReportSettings
            .AnyAsync(s => s.OrganizationSlug == org)).ShouldBeFalse();
    }

    #endregion

    #region Dataset responses

    [Test]
    public async Task TheDatasetInfo_HasNoReportsAndNoViewsInItsMeta()
    {
        var org = await NewOrganizationAsync();

        var reply = await SendAsync(HttpMethod.Get, $"/orgs/{org}/ds/{PrivateDs}", Owner);

        reply.Status.ShouldBe(200, reply.Text);
        var meta = reply.Json[0]!["properties"]!["meta"]!.AsObject();
        meta.ContainsKey("reports").ShouldBeFalse();
        meta.ContainsKey("views").ShouldBeFalse();
        meta["name"]!["data"]!.GetValue<string>().ShouldBe("Site");
    }

    #endregion
}
