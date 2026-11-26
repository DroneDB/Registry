using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.Organizations;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The logo of an organization through HTTP on <see cref="RegistryWebFactory"/>: who sets, removes and sees it, what an
/// upload accepts and what it becomes, how it is served and cached, where it is in the answers that show the
/// organization, and what happens to it with the organization.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~OrganizationLogoHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class OrganizationLogoHttpTests : IDisposable
{
    private const string Owner = "ol-owner";
    private const string OrgAdmin = "ol-orgadmin";
    private const string Writer = "ol-writer";
    private const string Reader = "ol-reader";
    private const string Outsider = "ol-outsider";
    private const string InstanceAdmin = "ol-instadmin";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];
    private static readonly byte[] WebP = [.. "RIFF"u8, 4, 0, 0, 0, .. "WEBP"u8, 1];

    private const string Svg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10" onload="alert(1)">
          <script>alert(2)</script>
          <image href="https://evil.test/x.png"/>
          <rect width="10" height="10" fill="#0070e0"/>
        </svg>
        """;

    private static readonly int MaxBytes = OrganizationLogos.MaxBytes;

    private readonly Dictionary<string, string> _ids = new();
    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private int _orgCounter;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var name in new[] { Owner, OrgAdmin, Writer, Reader, Outsider })
            _ids[name] = (await _factory.CreateUserAsync(name)).Id;
        _ids[InstanceAdmin] = (await _factory.CreateUserAsync(InstanceAdmin, isAdmin: true)).Id;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    #region Fixtures

    /// <summary>An organization of its own (so no test depends on another): an owner, an administrator, a writer and a reader, without datasets.</summary>
    private async Task<string> NewOrganizationAsync(bool isPublic = false)
    {
        var slug = $"ol-org-{++_orgCounter}";
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, slug, ownerId: _ids[Owner], isPublic: isPublic);
        foreach (var (user, permission) in new[]
                 {
                     (OrgAdmin, OrganizationPermissions.Admin), (Writer, OrganizationPermissions.ReadWrite),
                     (Reader, OrganizationPermissions.ReadOnly)
                 })
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

        public string? LogoUrl => Json["logoUrl"]?.GetValue<string>();

        public string? Header(string name) =>
            Response.Headers.TryGetValues(name, out var values) || Response.Content.Headers.TryGetValues(name, out values)
                ? string.Join(",", values)
                : null;
    }

    private async Task<Reply> SendAsync(HttpMethod method, string url, string? user, HttpContent? content = null,
        string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (user != null)
            request.AsUser(user, isAdmin: user == InstanceAdmin);
        if (ifNoneMatch != null)
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        var response = await _client.SendAsync(request);
        return new Reply((int)response.StatusCode, response, await response.Content.ReadAsByteArrayAsync());
    }

    private static string LogoRoute(string org) => $"/orgs/{org}/logo";

    private Task<Reply> PutLogo(string org, byte[] bytes, string? user = Owner, string field = "file",
        string fileName = "logo.png")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, field, fileName);
        return SendAsync(HttpMethod.Put, LogoRoute(org), user, form);
    }

    private Task<Reply> DeleteLogo(string org, string? user = Owner) =>
        SendAsync(HttpMethod.Delete, LogoRoute(org), user);

    private Task<Reply> GetLogo(string org, string? user = Owner, string? url = null, string? ifNoneMatch = null) =>
        SendAsync(HttpMethod.Get, url ?? LogoRoute(org), user, ifNoneMatch: ifNoneMatch);

    private Task<Reply> GetOrganization(string org, string? user = Owner) =>
        SendAsync(HttpMethod.Get, $"/orgs/{org}", user);

    private string AssetFolder(string org) => Path.Combine(_factory.StoragePath, "org-assets", org);

    private string LogoFile(string org) => Path.Combine(AssetFolder(org), "logo");

    // The clock of a file system is coarse (a few milliseconds): a logo set again at once could get the time of the one
    // before. A logo that is an hour old tells the version of a new one for certain.
    private void Age(string org) => File.SetLastWriteTimeUtc(LogoFile(org), DateTime.UtcNow.AddHours(-1));

    private static readonly Regex UrlShape = new(@"^/orgs/(?<slug>[^/]+)/logo\?v=(?<v>\d+)$", RegexOptions.None,
        TimeSpan.FromSeconds(5));

    private static string VersionOf(string? url)
    {
        var version = UrlShape.Match(url ?? "").Groups["v"].Value;
        version.ShouldNotBeEmpty($"'{url}' is not a versioned logo URL");
        return version;
    }

    #endregion

    #region Set, replace, remove

    [Test]
    public async Task ALogo_IsSet_ServedAsItWasSent_AndNamedByTheOrganization()
    {
        var org = await NewOrganizationAsync();

        var put = await PutLogo(org, Png);

        put.Status.ShouldBe(200, put.Text);
        put.Header("Content-Type").ShouldStartWith("application/json");
        UrlShape.IsMatch(put.LogoUrl ?? "").ShouldBeTrue(put.Text);
        put.LogoUrl.ShouldStartWith($"/orgs/{org}/logo?v=");
        File.ReadAllBytes(LogoFile(org)).ShouldBe(Png);
        Directory.GetFiles(AssetFolder(org)).Select(Path.GetFileName).ShouldBe(["logo"], "no temporary file is left");

        var served = await GetLogo(org, url: put.LogoUrl);
        served.Status.ShouldBe(200);
        served.Bytes.ShouldBe(Png);
        served.Header("Content-Type").ShouldBe("image/png");
        served.Header("X-Content-Type-Options").ShouldBe("nosniff");
        served.Header("Content-Security-Policy").ShouldBe("sandbox");
        served.Header("ETag").ShouldNotBeNull();

        (await GetOrganization(org)).Json["logoUrl"]!.GetValue<string>().ShouldBe(put.LogoUrl);
    }

    [Test]
    public async Task ReplacingTheLogo_ServesTheNewOne_UnderANewVersion()
    {
        var org = await NewOrganizationAsync();
        var first = (await PutLogo(org, Png)).LogoUrl;
        Age(org);
        var aged = (await GetOrganization(org)).Json["logoUrl"]!.GetValue<string>();
        VersionOf(aged).ShouldNotBe(VersionOf(first), "the version is the time of the file");

        var second = (await PutLogo(org, Jpeg)).LogoUrl;

        VersionOf(second).ShouldNotBe(VersionOf(aged));
        var served = await GetLogo(org, url: second);
        served.Bytes.ShouldBe(Jpeg);
        served.Header("Content-Type").ShouldBe("image/jpeg");
        Directory.GetFiles(AssetFolder(org)).Select(Path.GetFileName).ShouldBe(["logo"]);
    }

    [Test]
    public async Task ReplacingAPngWithAnSvg_LeavesOneFile_ServedAsSvg()
    {
        var org = await NewOrganizationAsync();
        await PutLogo(org, Png);

        var put = await PutLogo(org, Encoding.UTF8.GetBytes(Svg), fileName: "logo.png");

        put.Status.ShouldBe(200, put.Text);
        Directory.GetFiles(AssetFolder(org)).Select(Path.GetFileName).ShouldBe(["logo"], "one logo, whatever its type");
        var served = await GetLogo(org);
        served.Header("Content-Type").ShouldStartWith("image/svg+xml");
        served.Text.ShouldContain("<rect");
    }

    [Test]
    public async Task AnSvgLogo_IsStoredAndServedWithoutScripts()
    {
        var org = await NewOrganizationAsync();

        (await PutLogo(org, Encoding.UTF8.GetBytes(Svg))).Status.ShouldBe(200);

        var served = await GetLogo(org);
        served.Text.ShouldNotContain("script");
        served.Text.ShouldNotContain("alert");
        served.Text.ShouldNotContain("evil.test");
        File.ReadAllText(LogoFile(org)).ShouldBe(served.Text);
        served.Header("Content-Security-Policy").ShouldBe("sandbox");
    }

    [Test]
    public async Task TheTypeOfALogo_IsTheContents_NotTheNameOrTheTypeTheClientSent()
    {
        var org = await NewOrganizationAsync();

        // A PNG under a .svg name (and an image/png content type on every part) is what its bytes say
        await PutLogo(org, Png, fileName: "logo.svg");
        (await GetLogo(org)).Header("Content-Type").ShouldBe("image/png");
        await PutLogo(org, WebP, fileName: "logo.png");
        (await GetLogo(org)).Header("Content-Type").ShouldBe("image/webp");
        await PutLogo(org, Jpeg, fileName: "logo.webp");
        (await GetLogo(org)).Header("Content-Type").ShouldBe("image/jpeg");
    }

    [Test]
    public async Task RemovingTheLogo_DeletesTheFile_AndIsNoContentAlsoWhenThereIsNone()
    {
        var org = await NewOrganizationAsync();

        (await DeleteLogo(org)).Status.ShouldBe(204, "no logo is no error");

        var url = (await PutLogo(org, Png)).LogoUrl;
        var removed = await DeleteLogo(org, OrgAdmin);

        removed.Status.ShouldBe(204);
        File.Exists(LogoFile(org)).ShouldBeFalse();
        (await GetLogo(org, url: url)).Status.ShouldBe(404);
        (await GetOrganization(org)).Json["logoUrl"].ShouldBeNull();
        (await DeleteLogo(org)).Status.ShouldBe(204);
    }

    #endregion

    #region What an upload accepts

    [Test]
    public async Task ALogoOfExactlyTheLimit_IsAccepted_AndOneByteOverIs413()
    {
        var org = await NewOrganizationAsync();

        (await PutLogo(org, [.. Png, .. new byte[MaxBytes - Png.Length]])).Status.ShouldBe(200);
        var over = await PutLogo(org, [.. Png, .. new byte[MaxBytes - Png.Length + 1]]);
        over.Status.ShouldBe(413, over.Text);
        over.Header("Content-Type").ShouldStartWith("application/problem+json");

        // Far over: refused from its declared length, before it is read
        (await PutLogo(org, [.. Png, .. new byte[2 * 1024 * 1024]])).Status.ShouldBe(413);
        new FileInfo(LogoFile(org)).Length.ShouldBe(MaxBytes, "the refused uploads did not replace it");
    }

    [Test]
    public async Task WhatIsNotAnAllowedLogo_Is400_AndNothingIsStored()
    {
        var org = await NewOrganizationAsync();

        foreach (var bytes in new[]
                 {
                     Encoding.UTF8.GetBytes("GIF89a....."),
                     Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"),
                     Encoding.UTF8.GetBytes("just words"),
                     [.. "RIFF"u8, 4, 0, 0, 0, .. "WAVE"u8],
                     []
                 })
        {
            var reply = await PutLogo(org, bytes);
            reply.Status.ShouldBe(400, Encoding.UTF8.GetString(bytes));
            reply.Header("Content-Type").ShouldStartWith("application/problem+json");
        }

        (await SendAsync(HttpMethod.Put, LogoRoute(org), Owner, new MultipartFormDataContent())).Status.ShouldBe(400);
        (await PutLogo(org, Png, field: "image")).Status.ShouldBe(400);

        Directory.Exists(AssetFolder(org)).ShouldBeFalse();
        (await GetOrganization(org)).Json["logoUrl"].ShouldBeNull();
    }

    [Test]
    public async Task AnUnknownOrganization_Is404_ForEveryVerb()
    {
        (await PutLogo("ol-nothing", Png)).Status.ShouldBe(404);
        (await DeleteLogo("ol-nothing")).Status.ShouldBe(404);
        (await GetLogo("ol-nothing")).Status.ShouldBe(404);
    }

    #endregion

    #region Who may

    public static IEnumerable<TestCaseData> Managers()
    {
        foreach (var isPublic in new[] { false, true })
        {
            yield return new TestCaseData(isPublic, Owner, 200);
            yield return new TestCaseData(isPublic, InstanceAdmin, 200);
            yield return new TestCaseData(isPublic, OrgAdmin, 200);
            yield return new TestCaseData(isPublic, Writer, 403);
            yield return new TestCaseData(isPublic, Reader, 403);
            yield return new TestCaseData(isPublic, Outsider, 403);
            yield return new TestCaseData(isPublic, null, 401);
        }
    }

    [TestCaseSource(nameof(Managers))]
    public async Task OnlyTheAdministratorsOfTheOrganization_SetTheLogo(bool isPublic, string? user, int status)
    {
        var org = await NewOrganizationAsync(isPublic);

        var reply = await PutLogo(org, Png, user);

        reply.Status.ShouldBe(status, reply.Text);
        File.Exists(LogoFile(org)).ShouldBe(status == 200);
    }

    [TestCaseSource(nameof(Managers))]
    public async Task OnlyTheAdministratorsOfTheOrganization_RemoveTheLogo(bool isPublic, string? user, int status)
    {
        var org = await NewOrganizationAsync(isPublic);
        await PutLogo(org, Png);

        var reply = await DeleteLogo(org, user);

        reply.Status.ShouldBe(status == 200 ? 204 : status, reply.Text);
        File.Exists(LogoFile(org)).ShouldBe(status != 200);
    }

    [Test]
    public async Task TheLogo_IsSeenByWhoeverCanReadTheOrganization_AsTheOrganizationIsRead(
        [Values(false, true)] bool isPublic,
        [Values(Owner, InstanceAdmin, OrgAdmin, Writer, Reader, Outsider, null)] string? user)
    {
        var org = await NewOrganizationAsync(isPublic);
        await PutLogo(org, Png);

        var logo = await GetLogo(org, user);
        var organization = await GetOrganization(org, user);

        logo.Status.ShouldBe(organization.Status, $"{(isPublic ? "public" : "private")} organization, {user ?? "anonymous"}");
        if (logo.Status == 200)
            logo.Bytes.ShouldBe(Png);
        else
            logo.Bytes.ShouldNotBe(Png);

        // The two this makes certain of: a public organization shows its logo to anyone, a private one to strangers never
        if (isPublic)
            logo.Status.ShouldBe(200);
        else if (user is null)
            logo.Status.ShouldBe(401);
        else if (user == Outsider)
            logo.Status.ShouldBe(403);
    }

    [Test]
    public async Task TheLogoOfAPrivateOrganization_IsNotInTheBrandingFolder()
    {
        var org = await NewOrganizationAsync();
        await PutLogo(org, Png);

        foreach (var path in new[]
                 {
                     "/branding/logo", $"/branding/{org}/logo", $"/branding/org-assets/{org}/logo",
                     $"/branding/../org-assets/{org}/logo"
                 })
        {
            var reply = await SendAsync(HttpMethod.Get, path, null);
            reply.Bytes.ShouldNotBe(Png, path);
            reply.Header("Content-Type").ShouldNotStartWith("image/", Case.Insensitive, path);
        }

        Directory.GetFiles(Path.Combine(_factory.StoragePath, "branding"), "*", SearchOption.AllDirectories)
            .ShouldNotContain(f => Path.GetFileName(f) == "logo", "nothing of the organization is in the public folder");
    }

    #endregion

    #region Caching

    [Test]
    public async Task TheLogo_IsValidatedByItsTag_AndAnUnchangedOneIsNotModified()
    {
        var org = await NewOrganizationAsync();
        await PutLogo(org, Png);

        var first = await GetLogo(org);
        var etag = first.Header("ETag").ShouldNotBeNull();
        var again = await GetLogo(org, ifNoneMatch: etag);

        again.Status.ShouldBe(304);
        again.Bytes.ShouldBeEmpty();

        Age(org);
        var changed = await GetLogo(org, ifNoneMatch: etag);
        changed.Status.ShouldBe(200, "a logo written since is another representation");
        changed.Header("ETag").ShouldNotBe(etag);
    }

    [Test]
    public async Task AUrlWithTheCurrentVersion_IsKeptWithoutAsking_AnyOtherIsAskedAboutEachTime()
    {
        var org = await NewOrganizationAsync();
        var url = (await PutLogo(org, Png)).LogoUrl.ShouldNotBeNull();

        var current = await GetLogo(org, url: url);
        current.Header("Cache-Control").ShouldNotBeNull().ShouldSatisfyAllConditions(
            c => c.ShouldContain("immutable"), c => c.ShouldContain("private"), c => c.ShouldContain("max-age="));

        foreach (var other in new[] { LogoRoute(org), $"{LogoRoute(org)}?v=1", $"{LogoRoute(org)}?v=" })
        {
            var cache = (await GetLogo(org, url: other)).Header("Cache-Control").ShouldNotBeNull();
            cache.ShouldContain("no-cache", customMessage: other);
            cache.ShouldNotContain("immutable", customMessage: other);
        }

        // After a replacement the old URL is a stale one
        Age(org);
        await PutLogo(org, Jpeg);
        (await GetLogo(org, url: url)).Header("Cache-Control").ShouldNotBeNull().ShouldNotContain("immutable");
    }

    #endregion

    #region Where the logo is named

    [Test]
    public async Task AnOrganizationWithoutLogo_HasNoLogoUrl_AndALeftoverTemporaryFileIsNoLogo()
    {
        var org = await NewOrganizationAsync(isPublic: true);
        (await GetOrganization(org)).Json["logoUrl"].ShouldBeNull();

        // What a write that stopped halfway leaves is not the logo: only the file named logo is
        Directory.CreateDirectory(AssetFolder(org));
        File.WriteAllBytes(Path.Combine(AssetFolder(org), "logo.tmp"), Png);

        (await GetOrganization(org)).Json["logoUrl"].ShouldBeNull();
        (await GetLogo(org)).Status.ShouldBe(404);
        (await GetLogo(org, user: null)).Status.ShouldBe(404);
        OrganizationIn(await SendAsync(HttpMethod.Get, "/orgs", Owner), org)["logoUrl"].ShouldBeNull();
    }

    [Test]
    public async Task TheLogoUrl_IsInEveryAnswerThatShowsTheOrganization()
    {
        var org = await NewOrganizationAsync(isPublic: true);
        var url = (await PutLogo(org, Png)).LogoUrl.ShouldNotBeNull();

        (await GetOrganization(org)).Json["logoUrl"]!.GetValue<string>().ShouldBe(url);
        (await GetOrganization(org, null)).Json["logoUrl"]!.GetValue<string>().ShouldBe(url);
        foreach (var user in new[] { Owner, InstanceAdmin, Outsider })
            OrganizationIn(await SendAsync(HttpMethod.Get, "/orgs", user), org)["logoUrl"]!.GetValue<string>()
                .ShouldBe(url, $"the list of {user}");
        OrganizationIn(await SendAsync(HttpMethod.Get, "/orgs?ownedOnly=true", Owner), org)["logoUrl"]!
            .GetValue<string>().ShouldBe(url);
        OrganizationIn(await SendAsync(HttpMethod.Get, "/orgs/public", null), org)["logoUrl"]!.GetValue<string>()
            .ShouldBe(url);

        // The one it was created with has none
        var created = await SendAsync(HttpMethod.Post, "/orgs", Owner, new FormUrlEncodedContent(
            new Dictionary<string, string> { ["name"] = "Logo less", ["slug"] = "ol-logo-less" }));
        created.Status.ShouldBe(201, created.Text);
        created.Json["logoUrl"].ShouldBeNull();
    }

    private static JsonNode OrganizationIn(Reply list, string slug)
    {
        list.Status.ShouldBe(200, list.Text);
        return list.Json.AsArray().Single(o => o!["slug"]!.GetValue<string>() == slug)!;
    }

    #endregion

    #region With the organization

    [Test]
    public async Task DeletingTheOrganization_DeletesItsLogo()
    {
        var org = await NewOrganizationAsync();
        await PutLogo(org, Png);
        File.Exists(LogoFile(org)).ShouldBeTrue();

        var deleted = await SendAsync(HttpMethod.Delete, $"/orgs/{org}", Owner);

        deleted.Status.ShouldBe(204, deleted.Text);
        Directory.Exists(AssetFolder(org)).ShouldBeFalse();
    }

    [Test]
    public async Task MergingAnOrganization_DeletesTheLogoOfTheSourceAndKeepsThatOfTheDestination()
    {
        var source = await NewOrganizationAsync();
        var destination = await NewOrganizationAsync();
        await PutLogo(source, Png);
        await PutLogo(destination, Jpeg);

        var merged = await SendAsync(HttpMethod.Post, "/sys/merge-organizations", InstanceAdmin,
            new StringContent(new JsonObject { ["sourceOrgSlug"] = source, ["destinationOrgSlug"] = destination }.ToJsonString(),
                Encoding.UTF8, "application/json"));

        merged.Status.ShouldBe(200, merged.Text);
        merged.Json["sourceOrganizationDeleted"]!.GetValue<bool>().ShouldBeTrue();
        Directory.Exists(AssetFolder(source)).ShouldBeFalse();
        File.ReadAllBytes(LogoFile(destination)).ShouldBe(Jpeg);
    }

    #endregion
}
