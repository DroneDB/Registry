using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.UpdateCheck;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.UpdateCheck;

/// <summary>
/// The public URL of a deployment for the update check: which hosts count as public, the file the web node writes (on
/// change, at most once an hour), and the precedence the payload applies (configured URL, observed URL, none).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~ObservedBaseUrlTests"
/// </summary>
[TestFixture]
public class ObservedBaseUrlTests : IDisposable
{
    private TempStorage _storage = null!;
    private FakeTime _time = null!;

    [SetUp]
    public void SetUp()
    {
        _storage = new TempStorage();
        _time = new FakeTime(new DateTime(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc));
    }

    [TearDown]
    public void Dispose()
    {
        _storage?.Dispose();
        GC.SuppressFinalize(this);
    }

    private ObservedBaseUrlStore NewStore() => new(_storage.Storage, UpdateCheckTestKit.Disk, _time, NullLogger<ObservedBaseUrlStore>.Instance);

    private string ObservedFile => _storage.File(MagicStrings.ObservedBaseUrlFileName);

    [TestCase("https://registry.acme-survey.com", true)]
    [TestCase("http://registry.acme-survey.com:8080", true)]
    [TestCase("https://hub.example.org", true)]
    [TestCase("https://8.8.8.8", true)]
    [TestCase("https://[2606:4700:4700::1111]", true)]
    [TestCase("http://localhost", false)]
    [TestCase("http://localhost:5000", false)]
    [TestCase("http://app.localhost", false)]
    [TestCase("http://127.0.0.1:5000", false)]
    [TestCase("http://[::1]:5000", false)]
    [TestCase("http://10.1.2.3", false)]
    [TestCase("http://172.16.0.9", false)]
    [TestCase("http://172.32.0.9", true)]
    [TestCase("http://192.168.1.20", false)]
    [TestCase("http://169.254.169.254", false)]
    [TestCase("http://[fe80::1]", false)]
    [TestCase("http://[fd00::1]", false)]
    [TestCase("http://[::ffff:10.0.0.1]", false)]
    [TestCase("http://0.0.0.0", false)]
    [TestCase("http://100.64.0.1", false)]
    [TestCase("http://224.0.0.1", false)]
    [TestCase("http://registry", false)]
    [TestCase("http://my-nas", false)]
    [TestCase("ftp://registry.acme-survey.com", false)]
    [TestCase("not a url", false)]
    [TestCase("", false)]
    public void IsPublic_AcceptsOnlyHostsTheInternetCanReach(string url, bool expected) =>
        PublicHost.IsPublic(url).ShouldBe(expected);

    [Test]
    public void Observe_WritesTheUrl_AsJson()
    {
        NewStore().Observe("https://registry.acme-survey.com").ShouldBeTrue();

        using var json = JsonDocument.Parse(File.ReadAllText(ObservedFile));
        json.RootElement.GetProperty("baseUrl").GetString().ShouldBe("https://registry.acme-survey.com");
        json.RootElement.GetProperty("observedAt").GetString().ShouldBe("2026-10-25T08:00:00Z");
        NewStore().Read().ShouldBe("https://registry.acme-survey.com");
    }

    [Test]
    public void Observe_IgnoresTheHostsThatAreNotPublic()
    {
        var store = NewStore();

        store.Observe("http://localhost:5000").ShouldBeFalse();
        store.Observe("http://192.168.1.20").ShouldBeFalse();
        store.Observe("http://registry").ShouldBeFalse();

        File.Exists(ObservedFile).ShouldBeFalse();
    }

    [Test]
    public void Observe_WritesOnlyWhenTheValueChanges()
    {
        var store = NewStore();
        store.Observe("https://a.example.org").ShouldBeTrue();
        File.Delete(ObservedFile);

        store.Observe("https://a.example.org").ShouldBeFalse();
        _time.Advance(TimeSpan.FromHours(5));
        store.Observe("https://a.example.org").ShouldBeFalse();

        File.Exists(ObservedFile).ShouldBeFalse();
    }

    [Test]
    public void Observe_IsRateLimited_ToOneWriteAnHourPerNode()
    {
        var store = NewStore();
        store.Observe("https://a.example.org").ShouldBeTrue();

        _time.Advance(TimeSpan.FromMinutes(59));
        store.Observe("https://b.example.org").ShouldBeFalse();
        store.Read().ShouldBe("https://a.example.org");

        _time.Advance(TimeSpan.FromMinutes(2));
        store.Observe("https://b.example.org").ShouldBeTrue();
        store.Read().ShouldBe("https://b.example.org");
    }

    [Test]
    public void ARestartedNode_DoesNotRewriteTheUrlTheFileHolds()
    {
        NewStore().Observe("https://a.example.org").ShouldBeTrue();
        var before = File.GetLastWriteTimeUtc(ObservedFile);

        NewStore().Observe("https://a.example.org").ShouldBeFalse();

        File.GetLastWriteTimeUtc(ObservedFile).ShouldBe(before);
    }

    [TestCase("https://configured.example.org/", "https://observed.example.org", "https://configured.example.org")]
    [TestCase(null, "https://observed.example.org", "https://observed.example.org")]
    [TestCase("", "https://observed.example.org", "https://observed.example.org")]
    [TestCase(null, null, null)]
    public void BaseUrl_TheConfiguredUrlWins_ThenTheObservedOne_ThenNone(string? configured, string? observed, string? expected)
    {
        var store = NewStore();
        if (observed != null)
            store.Observe(observed);

        var baseUrl = new InstanceBaseUrl(Microsoft.Extensions.Options.Options.Create(new HttpHostOptions { ExternalUrlOverride = configured }), store);

        baseUrl.Current().ShouldBe(expected);
    }

    [Test]
    public void BaseUrl_DropsAValueThatDoesNotFitTheContract()
    {
        var tooLong = "https://example.org/" + new string('a', InstanceBaseUrl.MaxLength);

        new InstanceBaseUrl(Microsoft.Extensions.Options.Options.Create(new HttpHostOptions { ExternalUrlOverride = tooLong }), NewStore()).Current().ShouldBeNull();
        new InstanceBaseUrl(Microsoft.Extensions.Options.Options.Create(new HttpHostOptions { ExternalUrlOverride = "ftp://example.org" }), NewStore()).Current().ShouldBeNull();
        new InstanceBaseUrl(Microsoft.Extensions.Options.Options.Create(new HttpHostOptions { ExternalUrlOverride = "https://exa mple.org" }), NewStore()).Current().ShouldBeNull();
    }

    private static async Task<RegistryWebFactory> Request(string host, IDictionary<string, string>? settings = null)
    {
        var factory = new RegistryWebFactory(settings);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sys/features");
        request.Headers.Host = host;
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return factory;
    }

    [Test]
    public async Task AnExternalRequest_RecordsTheUrlItWasServedOn()
    {
        using var factory = await Request("registry.acme-survey.com");

        var file = Path.Combine(factory.StoragePath, MagicStrings.ObservedBaseUrlFileName);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        json.RootElement.GetProperty("baseUrl").GetString().ShouldBe("http://registry.acme-survey.com");
    }

    [TestCase("localhost:5000")]
    [TestCase("127.0.0.1")]
    [TestCase("192.168.1.20:8080")]
    [TestCase("registry")]
    public async Task ARequestOnAHostThatIsNotPublic_RecordsNothing(string host)
    {
        using var factory = await Request(host);

        File.Exists(Path.Combine(factory.StoragePath, MagicStrings.ObservedBaseUrlFileName)).ShouldBeFalse();
    }

    [Test]
    public async Task WithAnExternalUrlConfigured_NothingIsRecorded()
    {
        using var factory = await Request("registry.acme-survey.com",
            new Dictionary<string, string> { ["AppSettings:ExternalUrlOverride"] = "https://hub.example.org" });

        File.Exists(Path.Combine(factory.StoragePath, MagicStrings.ObservedBaseUrlFileName)).ShouldBeFalse();
    }
}
