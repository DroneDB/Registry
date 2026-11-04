using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.UpdateCheck;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.UpdateCheck;

/// <summary>
/// The body of the update check request: every field of the contract, from the identity, the build, the machine, the
/// configuration and the administrator's declaration; and the guarantee that it always matches the JSON Schema.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UpdateCheckPayloadTests"
/// </summary>
[TestFixture]
public class UpdateCheckPayloadTests : IDisposable
{
    private TempStorage _storage = null!;
    private UpdateCheckSettingsStore _settings = null!;
    private ObservedBaseUrlStore _observed = null!;
    private Mock<IPlatformFacts> _platform = null!;
    private PlatformSnapshot _snapshot = null!;
    private Mock<IDdbWrapper> _ddb = null!;

    [SetUp]
    public void SetUp()
    {
        _storage = new TempStorage();
        _settings = new UpdateCheckSettingsStore(_storage.Storage, UpdateCheckTestKit.Disk, NullLogger<UpdateCheckSettingsStore>.Instance);
        _observed = new ObservedBaseUrlStore(_storage.Storage, UpdateCheckTestKit.Disk, TimeProvider.System, NullLogger<ObservedBaseUrlStore>.Instance);
        _snapshot = new PlatformSnapshot("linux", "Ubuntu 22.04.4 LTS", "x64", ".NET 10.0.9", true, "SRV-GIS-01", "acme.internal");
        _platform = new Mock<IPlatformFacts>();
        _platform.Setup(p => p.Capture()).Returns(() => _snapshot);
        _ddb = new Mock<IDdbWrapper>();
        _ddb.Setup(d => d.GetVersion()).Returns("2.0.0 1f171e5");
    }

    [TearDown]
    public void Dispose()
    {
        _storage?.Dispose();
        GC.SuppressFinalize(this);
    }

    private UpdateCheckPayloadBuilder Builder(InstanceType instanceType = InstanceType.Default, DbProvider db = DbProvider.Sqlite,
        string? externalUrl = null, IPlatformFacts? platform = null)
    {
        var host = HostResourceBudget.Create(instanceType, new HostFacts(4, 8L << 30), new HostResourceOptions(),
            new DdbRuntimeOptions(), _ => null);
        return new UpdateCheckPayloadBuilder(new FixedInstanceIdentity(), _settings,
            new InstanceBaseUrl(Microsoft.Extensions.Options.Options.Create(new HttpHostOptions { ExternalUrlOverride = externalUrl }), _observed),
            platform ?? _platform.Object, Microsoft.Extensions.Options.Options.Create(new DataStoreOptions { RegistryProvider = db }), host, _ddb.Object);
    }

    private static string Json(UpdateCheckPayload payload) => ApiJson.Serialize(payload);

    private static void ShouldMatchTheContract(string json) =>
        ContractSchema.Payload.Violations(json).ShouldBeEmpty($"the payload does not match the schema: {json}");

    [Test]
    public void ThePayload_MatchesTheCanonicalSchema()
    {
        _settings.Update(s => s with { UseDeclaration = "commercial_needs_license" });
        _observed.Observe("https://registry.acme-survey.com");

        ShouldMatchTheContract(Json(Builder().Build()));
    }

    [Test]
    public void ThePayload_OfARealMachine_MatchesTheCanonicalSchema() =>
        ShouldMatchTheContract(Json(Builder(platform: new PlatformFacts(UpdateCheckTestKit.Disk)).Build()));

    [Test]
    public void TheExampleOfTheContract_MatchesTheCanonicalSchema() =>
        ShouldMatchTheContract(File.ReadAllText(ContractSchema.PathOf("examples/update-check-payload.example.json")));

    [Test]
    public void TheSchema_RejectsAPayloadThatBreaksTheContract()
    {
        ContractSchema.Payload.Violations("""{"schemaVersion":2,"instanceId":"x","version":"3.1"}""").ShouldNotBeEmpty();
        ContractSchema.Payload.Violations("""{"schemaVersion":1,"instanceId":"a1b2c3d4-5e6f-4a7b-8c9d-0e1f2a3b4c5d","version":"3.1.0","os":"macos"}""")
            .ShouldNotBeEmpty();
    }

    [Test]
    public void ThePayload_CarriesEveryField_FromItsSource()
    {
        _settings.Update(s => s with { UseDeclaration = "evaluation" });
        _observed.Observe("https://registry.acme-survey.com");

        var payload = Builder(InstanceType.ProcessingNode, DbProvider.Mysql).Build();

        payload.SchemaVersion.ShouldBe(1);
        payload.InstanceId.ShouldBe(FixedInstanceIdentity.DefaultId);
        payload.InstalledAt.ShouldBe(new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc));
        payload.Version.ShouldBe(RegistryVersion.Current);
        payload.Os.ShouldBe("linux");
        payload.OsCaption.ShouldBe("Ubuntu 22.04.4 LTS");
        payload.Arch.ShouldBe("x64");
        payload.Runtime.ShouldBe(".NET 10.0.9");
        payload.Env.ShouldBe("docker");
        payload.InstanceType.ShouldBe("processingNode");
        payload.Db.ShouldBe("mysql");
        payload.BaseUrl.ShouldBe("https://registry.acme-survey.com");
        payload.Hostname.ShouldBe("SRV-GIS-01");
        payload.Domain.ShouldBe("acme.internal");
        payload.LicenseRef.ShouldBeNull();
        payload.UseDeclaration.ShouldBe("evaluation");
    }

    [TestCase(InstanceType.Default, "default")]
    [TestCase(InstanceType.ProcessingNode, "processingNode")]
    [TestCase(InstanceType.WebServer, "webServer")]
    [TestCase(InstanceType.ThumbnailGenerator, "thumbnailGenerator")]
    public void InstanceType_MirrorsTheConfiguration(InstanceType configured, string expected) =>
        Builder(configured).Build().InstanceType.ShouldBe(expected);

    [Test]
    public void Version_IsTheRegistryVersion_AndDdbVersionTheOneThisBuildIsPinnedTo()
    {
        var payload = Builder().Build();

        payload.Version.ShouldBe(RegistryVersion.Current);
        payload.Version.ShouldMatch(@"^\d+\.\d+\.\d+$");
        payload.DdbVersion.ShouldNotBeNull().ShouldBe(LibddbVersion.Pinned);
        payload.DdbVersion.ShouldMatch(@"^\d+\.\d+(\.\d+)?$");
        _ddb.Verify(d => d.GetVersion(), Times.Never);
    }

    [Test]
    public void ThePinnedVersion_IsTheOneOfTheBuildProperties()
    {
        var pin = System.Xml.Linq.XDocument.Load(RepositoryFiles.PathOf("Directory.Build.props"))
            .Descendants("DdbVersion").Single().Value;

        LibddbVersion.Pinned.ShouldBe(pin);
    }

    [TestCase("2.0.0 1f171e5", "2.0.0")]
    [TestCase("2.0.0", "2.0.0")]
    [TestCase("1.12 abc", "1.12")]
    [TestCase("", null)]
    [TestCase(null, null)]
    public void LibddbSemver_IsThePartBeforeTheCommit(string? raw, string? expected) =>
        LibddbVersion.Semver(raw).ShouldBe(expected);

    [Test]
    public void ContactEmail_IsNotSent_WithoutConsent()
    {
        _settings.Update(s => s with { ContactEmail = "it@acme-survey.com", ContactConsentAt = null });

        var json = Json(Builder().Build());

        json.ShouldNotContain("contactEmail");
        json.ShouldNotContain("it@acme-survey.com");
        ShouldMatchTheContract(json);
    }

    [Test]
    public void ContactEmail_IsSent_WithConsent()
    {
        _settings.Update(s => s with { ContactEmail = "it@acme-survey.com", ContactConsentAt = DateTime.UtcNow });

        var json = Json(Builder().Build());

        JsonDocument.Parse(json).RootElement.GetProperty("contactEmail").GetString().ShouldBe("it@acme-survey.com");
        ShouldMatchTheContract(json);
    }

    [Test]
    public void ValuesTheMachineDoesNotHave_AreSentAsNull()
    {
        _snapshot = _snapshot with { OsDescription = null, HostName = null, DomainName = null };

        var root = JsonDocument.Parse(Json(Builder().Build())).RootElement;

        foreach (var name in new[] { "osCaption", "hostname", "domain", "baseUrl", "licenseRef", "useDeclaration" })
            root.GetProperty(name).ValueKind.ShouldBe(JsonValueKind.Null, name);
        root.TryGetProperty("contactEmail", out _).ShouldBeFalse();
    }

    [Test]
    public void ConfiguredExternalUrl_WinsOverTheObservedOne()
    {
        _observed.Observe("https://observed.example.org");

        Builder(externalUrl: "https://configured.example.org").Build().BaseUrl.ShouldBe("https://configured.example.org");
        Builder().Build().BaseUrl.ShouldBe("https://observed.example.org");
    }

    [Test]
    public void TextsAreCutAndStripped_SoThePayloadAlwaysMatchesTheContract()
    {
        _snapshot = new PlatformSnapshot("windows", "Windows\u0007 10.0.\n22631 " + new string('x', 80), "arm64",
            ".NET " + new string('9', 60), false, "DESK\u0000TOP-" + new string('H', 100),
            "ac me.internal\u0001/" + new string('d', 300));

        var payload = Builder().Build();
        var json = Json(payload);

        payload.OsCaption!.Length.ShouldBe(UpdateCheckPayloadBuilder.MaxDescriptionLength);
        payload.OsCaption.ShouldNotContain("\u0007");
        payload.Runtime.Length.ShouldBeLessThanOrEqualTo(UpdateCheckPayloadBuilder.MaxDescriptionLength);
        payload.Hostname!.Length.ShouldBeLessThanOrEqualTo(UpdateCheckPayloadBuilder.MaxHostNameLength);
        payload.Hostname.ShouldStartWith("DESKTOP-HHHH");
        payload.Domain!.Length.ShouldBeLessThanOrEqualTo(UpdateCheckPayloadBuilder.MaxDomainLength);
        payload.Domain.ShouldStartWith("acme.internaldd");
        payload.Domain.ShouldMatch(@"^[A-Za-z0-9._-]+$");
        ShouldMatchTheContract(json);
    }

    [Test]
    public void ACutNeverSplitsASurrogatePair()
    {
        var text = new string('a', UpdateCheckPayloadBuilder.MaxDescriptionLength - 1) + "\U0001F600tail";

        var fitted = UpdateCheckPayloadBuilder.Fit(text, UpdateCheckPayloadBuilder.MaxDescriptionLength)!;

        fitted.ShouldBe(new string('a', UpdateCheckPayloadBuilder.MaxDescriptionLength - 1));
        Should.NotThrow(() => new System.Text.UTF8Encoding(false, true).GetBytes(fitted));
    }

    [Test]
    public void ASystemTheContractHasNoNameFor_IsLeftOutRatherThanSentInvalid()
    {
        _snapshot = _snapshot with { Os = null };

        var json = Json(Builder().Build());

        ShouldMatchTheContract(json);
        JsonDocument.Parse(json).RootElement.TryGetProperty("os", out _).ShouldBeFalse();
    }

    [Test]
    public void AnArchitectureTheContractHasNoNameFor_IsLeftOutRatherThanSentInvalid()
    {
        _snapshot = _snapshot with { Architecture = "riscv64" };

        var json = Json(Builder().Build());

        ShouldMatchTheContract(json);
        JsonDocument.Parse(json).RootElement.TryGetProperty("arch", out _).ShouldBeFalse();
    }

    [Test]
    public void ADomainThatHasNoValidCharacter_IsNull()
    {
        _snapshot = _snapshot with { DomainName = "()/ " };

        Builder().Build().Domain.ShouldBeNull();
    }
}
