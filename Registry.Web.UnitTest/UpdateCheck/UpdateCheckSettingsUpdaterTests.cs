#nullable enable
using System;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Services.UpdateCheck;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.UpdateCheck;

/// <summary>
/// What an administrator tells the update check: validation, the consent that goes with an e-mail address, and the one
/// check that a change queues (at most every 10 minutes, and none while the check is off).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UpdateCheckSettingsUpdaterTests"
/// </summary>
[TestFixture]
public class UpdateCheckSettingsUpdaterTests : IDisposable
{
    private TempStorage _storage = null!;
    private FakeTime _time = null!;
    private UpdateCheckSettingsStore _store = null!;
    private Mock<IBackgroundJobClient> _jobs = null!;

    [SetUp]
    public void SetUp()
    {
        _storage = new TempStorage();
        _time = new FakeTime(new DateTime(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc));
        _store = new UpdateCheckSettingsStore(_storage.Storage, NullLogger<UpdateCheckSettingsStore>.Instance);
        _jobs = new Mock<IBackgroundJobClient>();
        _jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("1");
    }

    [TearDown]
    public void Dispose()
    {
        _storage?.Dispose();
        GC.SuppressFinalize(this);
    }

    private UpdateCheckSettingsUpdater Updater(UpdateCheckOptions? options = null) => new(_store, _jobs.Object,
        Microsoft.Extensions.Options.Options.Create(options ?? new UpdateCheckOptions()), _time, NullLogger<UpdateCheckSettingsUpdater>.Instance);

    private void VerifyQueued(int times) => _jobs.Verify(j => j.Create(
        It.Is<Job>(job => job.Type == typeof(IUpdateCheckService) && job.Method.Name == nameof(IUpdateCheckService.CheckForUpdatesAsync)),
        It.IsAny<IState>()), Times.Exactly(times));

    private static UpdateCheckSettingsDto Body(string? declaration = null, string? email = null, bool consent = false) =>
        new() { UseDeclaration = declaration, ContactEmail = email, ContactConsent = consent };

    [Test]
    public void ADeclaration_IsSaved()
    {
        Updater().Apply(Body("commercial_needs_license"));

        _store.Read().UseDeclaration.ShouldBe("commercial_needs_license");
    }

    [TestCase("personal")]
    [TestCase("nonprofit")]
    [TestCase("evaluation")]
    [TestCase("commercial_licensed")]
    [TestCase("commercial_needs_license")]
    public void EveryKnownDeclaration_IsAccepted(string declaration)
    {
        Updater().Apply(Body(declaration));

        _store.Read().UseDeclaration.ShouldBe(declaration);
    }

    [TestCase("hobbyist")]
    [TestCase("COMMERCIAL_LICENSED")]
    [TestCase("personal use")]
    public void AnUnknownDeclaration_IsABadRequest_AndSavesNothing(string declaration)
    {
        Should.Throw<BadRequestException>(() => Updater().Apply(Body(declaration)));

        _store.Read().ShouldBe(UpdateCheckSettings.Empty);
        VerifyQueued(0);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("  ")]
    public void AnEmptyDeclaration_ClearsIt(string? empty)
    {
        Updater().Apply(Body("personal"));

        Updater().Apply(Body(empty));

        _store.Read().UseDeclaration.ShouldBeNull();
    }

    [Test]
    public void AnEmailWithoutConsent_IsABadRequest_AndSavesNothing()
    {
        var error = Should.Throw<BadRequestException>(() => Updater().Apply(Body("personal", "it@acme-survey.com", consent: false)));

        error.Message.ShouldContain("contactConsent");
        _store.Read().ShouldBe(UpdateCheckSettings.Empty);
        VerifyQueued(0);
    }

    [TestCase("not an address")]
    [TestCase("it@")]
    [TestCase("@acme.com")]
    [TestCase("Jane <jane@acme.com>")]
    [TestCase("a@b.com, c@d.com")]
    public void AnInvalidEmail_IsABadRequest(string email) =>
        Should.Throw<BadRequestException>(() => Updater().Apply(Body(null, email, consent: true)));

    [Test]
    public void AnEmailOver254Characters_IsABadRequest()
    {
        var email = new string('a', 250) + "@acme.com";

        Should.Throw<BadRequestException>(() => Updater().Apply(Body(null, email, consent: true)));
    }

    [Test]
    public void AnEmailWithConsent_IsSaved_WithTheTimeOfTheConsent()
    {
        Updater().Apply(Body(null, " it@acme-survey.com ", consent: true));

        var settings = _store.Read();
        settings.ContactEmail.ShouldBe("it@acme-survey.com");
        settings.ContactConsentAt.ShouldBe(_time.Now);
        settings.ConsentedEmail.ShouldBe("it@acme-survey.com");
    }

    [Test]
    public void AnEmptyEmail_RemovesTheEmail_AndTheConsent()
    {
        Updater().Apply(Body(null, "it@acme-survey.com", consent: true));

        Updater().Apply(Body(null, "", consent: false));

        var settings = _store.Read();
        settings.ContactEmail.ShouldBeNull();
        settings.ContactConsentAt.ShouldBeNull();
        settings.ConsentedEmail.ShouldBeNull();
    }

    [Test]
    public void TheConsent_KeepsItsTime_UnlessTheAddressChanges()
    {
        Updater().Apply(Body(null, "it@acme-survey.com", consent: true));
        var first = _time.Now;
        _time.Advance(TimeSpan.FromDays(2));

        Updater().Apply(Body("personal", "it@acme-survey.com", consent: true));
        _store.Read().ContactConsentAt.ShouldBe(first);

        _time.Advance(TimeSpan.FromDays(2));
        Updater().Apply(Body("personal", "ops@acme-survey.com", consent: true));
        _store.Read().ContactConsentAt.ShouldBe(_time.Now);
    }

    // ---- the check that a change queues

    [Test]
    public void AChange_QueuesOneCheck()
    {
        Updater().Apply(Body("commercial_needs_license"));

        VerifyQueued(1);
        _store.Read().LastPingEnqueuedAt.ShouldBe(_time.Now);
    }

    [Test]
    public void TheCheck_IsQueuedOnlyAfterTheNewSettingsAreInTheFile()
    {
        // A worker may run the check the moment it is queued, on any node: it must read the settings just saved
        Updater().Apply(Body("personal"));
        _time.Advance(UpdateCheckSettingsUpdater.PingWindow);
        UpdateCheckSettings? seenByWorker = null;
        _jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback(() => seenByWorker = new UpdateCheckSettingsStore(_storage.Storage, NullLogger<UpdateCheckSettingsStore>.Instance).Read())
            .Returns("2");

        Updater().Apply(Body("commercial_needs_license", "it@acme-survey.com", consent: true));

        seenByWorker.ShouldNotBeNull();
        seenByWorker.UseDeclaration.ShouldBe("commercial_needs_license");
        seenByWorker.ConsentedEmail.ShouldBe("it@acme-survey.com");
        seenByWorker.LastPingEnqueuedAt.ShouldBe(_time.Now);
    }

    [Test]
    public void ACheckThatCannotBeQueued_LeavesTheTimeOfTheLastQueuedCheck()
    {
        Updater().Apply(Body("personal"));
        var queuedAt = _time.Now;
        _time.Advance(UpdateCheckSettingsUpdater.PingWindow);
        _jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Throws(new InvalidOperationException("storage down"));

        Updater().Apply(Body("nonprofit"));

        _store.Read().UseDeclaration.ShouldBe("nonprofit");
        _store.Read().LastPingEnqueuedAt.ShouldBe(queuedAt);
    }

    [Test]
    public void ASecondChangeWithinTenMinutes_QueuesNothing()
    {
        Updater().Apply(Body("personal"));
        _time.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));

        Updater().Apply(Body("nonprofit"));

        VerifyQueued(1);
        _store.Read().UseDeclaration.ShouldBe("nonprofit");
    }

    [Test]
    public void AChangeAfterTenMinutes_QueuesAnother()
    {
        Updater().Apply(Body("personal"));
        _time.Advance(UpdateCheckSettingsUpdater.PingWindow);

        Updater().Apply(Body("nonprofit"));

        VerifyQueued(2);
    }

    [Test]
    public void TheLimit_IsSharedBetweenNodes_ThroughTheFile()
    {
        Updater().Apply(Body("personal"));
        var otherNode = new UpdateCheckSettingsStore(_storage.Storage, NullLogger<UpdateCheckSettingsStore>.Instance);
        var otherJobs = new Mock<IBackgroundJobClient>();

        new UpdateCheckSettingsUpdater(otherNode, otherJobs.Object, Microsoft.Extensions.Options.Options.Create(new UpdateCheckOptions()), _time,
            NullLogger<UpdateCheckSettingsUpdater>.Instance).Apply(Body("nonprofit"));

        otherJobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Test]
    public void SavingTheSameValues_QueuesNothing()
    {
        Updater().Apply(Body("personal", "it@acme-survey.com", consent: true));
        _time.Advance(TimeSpan.FromHours(1));

        Updater().Apply(Body("personal", "it@acme-survey.com", consent: true));

        VerifyQueued(1);
    }

    [Test]
    public void WhenTheCheckIsDisabled_TheSettingsAreSaved_ButNothingIsQueued()
    {
        Updater(new UpdateCheckOptions { UpdateCheckCron = "disabled" }).Apply(Body("personal"));

        _store.Read().UseDeclaration.ShouldBe("personal");
        VerifyQueued(0);
        _store.Read().LastPingEnqueuedAt.ShouldBeNull();
    }

    [Test]
    public void ACheckThatCannotBeQueued_DoesNotFailTheSave_NorUseUpTheWindow()
    {
        _jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Throws(new InvalidOperationException("storage down"));

        Updater().Apply(Body("personal"));

        _store.Read().UseDeclaration.ShouldBe("personal");
        _store.Read().LastPingEnqueuedAt.ShouldBeNull();
    }

    [Test]
    public void AFileOfSettings_IsReadBackAsTheContractNamesIt()
    {
        Updater().Apply(Body("commercial_needs_license", "it@acme-survey.com", consent: true));

        var json = System.IO.File.ReadAllText(_storage.File(MagicStrings.UpdateCheckSettingsFileName));
        json.ShouldContain("\"useDeclaration\":\"commercial_needs_license\"");
        json.ShouldContain("\"contactEmail\":\"it@acme-survey.com\"");
        json.ShouldContain("\"contactConsentAt\":\"2026-10-25T08:00:00Z\"");
    }
}
