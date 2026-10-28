#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Models.Email;
using Registry.Web.Services.Email;
using Registry.Web.Services.Email.Templates;
using Registry.Web.Services.Ports;
using Registry.Web.Services.UpdateCheck;
using Shouldly;

namespace Registry.Web.Test.UpdateCheck;

/// <summary>
/// The update e-mail: which mode sends for which update, to whom, and what is remembered after each outcome of the send.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UpdateNotifierTests"
/// </summary>
[TestFixture]
public class UpdateNotifierTests
{
    private static readonly EmailAddress Jane = new("jane@example.com");
    private static readonly EmailAddress John = new("john@example.com");

    private sealed class MemoryCache : IUpdateCheckCache
    {
        public UpdateCheckCache Content { get; private set; } = UpdateCheckCache.Empty;
        public UpdateCheckCache Read() => Content;
        public void Update(Func<UpdateCheckCache, UpdateCheckCache> change) => Content = change(Content);
    }

    private sealed class Admins : IAdminRecipients
    {
        public IReadOnlyList<EmailAddress> Addresses { get; set; } = [Jane, John];
        public int Calls { get; private set; }

        public Task<IReadOnlyList<EmailAddress>> GetAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Addresses);
        }
    }

    private sealed record Sent(object Template, UpdateAvailableEmailModel Model, IReadOnlyList<EmailAddress> To);

    private sealed class FakeEmail : IEmailService
    {
        public bool IsEnabled { get; set; } = true;
        public bool Result { get; set; } = true;
        public Exception? Failure { get; set; }
        public List<Sent> Sends { get; } = [];

        public Task<bool> SendNowAsync<TModel>(EmailTemplate<TModel> emailTemplate, TModel model, IReadOnlyList<EmailAddress> recipients,
            CancellationToken ct = default)
        {
            Sends.Add(new Sent(emailTemplate, (UpdateAvailableEmailModel)(object)model!, recipients));
            return Failure is null ? Task.FromResult(Result) : Task.FromException<bool>(Failure);
        }

        public Task<bool> SendNowAsync(EmailMessage message, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<string?> QueueAsync<TModel>(EmailTemplate<TModel> emailTemplate, TModel model, IReadOnlyList<EmailAddress> recipients,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<string?> QueueAsync(EmailMessage message, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private FakeEmail _email = null!;
    private Admins _admins = null!;
    private MemoryCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        _email = new FakeEmail();
        _admins = new Admins();
        _cache = new MemoryCache();
    }

    private UpdateNotifier Notifier(string? mode = "security", ILogger<UpdateNotifier>? logger = null) =>
        new(UpdateCheckTestKit.Configure(new UpdateCheckOptions { UpdateCheckEmail = mode! }), _email, _admins, _cache,
            logger ?? NullLogger<UpdateNotifier>.Instance);

    private static UpdateInfoDto Update(string latest = "3.1.0", bool security = false, bool available = true) => new()
    {
        CurrentVersion = "3.0.0",
        LatestVersion = latest,
        IsUpdateAvailable = available,
        IsSecurityUpdate = security,
        ReleaseNotes = "## Fixed\n- A fix (v3.1.0)\n\nAll changes: https://example.org/compare",
        CompareUrl = "https://example.org/compare"
    };

    [Test]
    public async Task Off_NeverSends_AndDoesNotEvenReadTheAdmins()
    {
        await Notifier("off").NotifyAsync(Update(security: true), CancellationToken.None);

        _email.Sends.ShouldBeEmpty();
        _admins.Calls.ShouldBe(0);
        _cache.Content.LastNotifiedVersion.ShouldBeNull();
    }

    [TestCase("")]
    [TestCase("sometimes")]
    public async Task AValueThatIsNoMode_SendsNothing(string mode)
    {
        await Notifier(mode).NotifyAsync(Update(security: true), CancellationToken.None);

        _email.Sends.ShouldBeEmpty();
    }

    [Test]
    public async Task Security_SendsForASecurityUpdate_ToAllAdminsInOneMessage_AndRecordsTheVersion()
    {
        await Notifier("security").NotifyAsync(Update(security: true), CancellationToken.None);

        var sent = _email.Sends.ShouldHaveSingleItem();
        sent.To.ShouldBe([Jane, John]);
        sent.Template.ShouldBeSameAs(UpdateAvailableEmailTemplate.Instance);
        sent.Model.ShouldBe(new UpdateAvailableEmailModel("3.0.0", "3.1.0", true, Update().ReleaseNotes, "https://example.org/compare"));
        _cache.Content.LastNotifiedVersion.ShouldBe("3.1.0");
    }

    [Test]
    public async Task Security_IsTheDefaultOfTheOptions()
    {
        new UpdateCheckOptions().EmailMode.ShouldBe(UpdateCheckEmailMode.Security);

        await Notifier(new UpdateCheckOptions().UpdateCheckEmail).NotifyAsync(Update(security: true), CancellationToken.None);

        _email.Sends.ShouldHaveSingleItem();
    }

    [Test]
    public async Task Security_SendsNothing_ForAnUpdateWithoutSecurityFixes()
    {
        await Notifier("security").NotifyAsync(Update(security: false), CancellationToken.None);

        _email.Sends.ShouldBeEmpty();
        _cache.Content.LastNotifiedVersion.ShouldBeNull();
    }

    [Test]
    public async Task All_SendsForAnUpdateWithoutSecurityFixes_Too()
    {
        await Notifier("ALL").NotifyAsync(Update(security: false), CancellationToken.None);

        _email.Sends.ShouldHaveSingleItem().Model.IsSecurityUpdate.ShouldBeFalse();
        _cache.Content.LastNotifiedVersion.ShouldBe("3.1.0");
    }

    [TestCase("all")]
    [TestCase("security")]
    public async Task NothingIsSent_WhenNoUpdateIsAvailable(string mode)
    {
        await Notifier(mode).NotifyAsync(Update(security: true, available: false), CancellationToken.None);

        _email.Sends.ShouldBeEmpty();
    }

    [Test]
    public async Task NothingIsSent_WhenTheCheckFoundNoVersion()
    {
        var update = Update();
        update.LatestVersion = null;

        await Notifier("all").NotifyAsync(update, CancellationToken.None);

        _email.Sends.ShouldBeEmpty();
    }

    [TestCase("all")]
    [TestCase("security")]
    public async Task OneEmailPerVersion_AlsoAfterARestart(string mode)
    {
        await Notifier(mode).NotifyAsync(Update(security: true), CancellationToken.None);

        // A new process: a new notifier on the same remembered file, and the same answer of the next daily check
        await Notifier(mode).NotifyAsync(Update(security: true), CancellationToken.None);
        await Notifier(mode).NotifyAsync(Update(security: true), CancellationToken.None);

        _email.Sends.ShouldHaveSingleItem();
    }

    [Test]
    public async Task ANewerVersion_IsToldAbout_AnOlderOneIsNot()
    {
        _cache.Update(c => c with { LastNotifiedVersion = "3.1.0" });
        var notifier = Notifier("all");

        await notifier.NotifyAsync(Update("3.1.0"), CancellationToken.None);
        await notifier.NotifyAsync(Update("3.0.5"), CancellationToken.None);
        _email.Sends.ShouldBeEmpty();

        await notifier.NotifyAsync(Update("3.2.0"), CancellationToken.None);

        _email.Sends.ShouldHaveSingleItem().Model.LatestVersion.ShouldBe("3.2.0");
        _cache.Content.LastNotifiedVersion.ShouldBe("3.2.0");
    }

    [Test]
    public async Task ADamagedLastNotifiedVersion_CountsAsNone()
    {
        _cache.Update(c => c with { LastNotifiedVersion = "not a version" });

        await Notifier("all").NotifyAsync(Update(), CancellationToken.None);

        _email.Sends.ShouldHaveSingleItem();
        _cache.Content.LastNotifiedVersion.ShouldBe("3.1.0");
    }

    [Test]
    public async Task Recording_KeepsTheLastResultOfTheCheck()
    {
        var result = new UpdateCheckResult(new UpdateCheckResponse("3.1.0", null, null, null, [], false, null), DateTime.UtcNow);
        _cache.Update(c => c with { LastResult = result });

        await Notifier("all").NotifyAsync(Update(), CancellationToken.None);

        _cache.Content.LastResult.ShouldBeSameAs(result);
    }

    [Test]
    public async Task EmailOff_StopsBeforeTheAdminsAreReadAndTheEmailIsRendered_LogsAtDebug_AndRecordsNothing()
    {
        _email.IsEnabled = false;
        var logger = new Mock<ILogger<UpdateNotifier>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        await Notifier("all", logger.Object).NotifyAsync(Update(), CancellationToken.None);

        _email.Sends.ShouldBeEmpty();
        _admins.Calls.ShouldBe(0);
        _cache.Content.LastNotifiedVersion.ShouldBeNull();
        logger.Invocations.Where(i => i.Method.Name == nameof(ILogger.Log)).Select(i => i.Arguments[0].ShouldBeOfType<LogLevel>())
            .ShouldBe([LogLevel.Debug]);
    }

    [Test]
    public async Task EmailSwitchedOffBetweenTheCheckAndTheSend_RecordsNothing_SoALaterCheckSends()
    {
        _email.Result = false;
        var notifier = Notifier("all");

        await notifier.NotifyAsync(Update(), CancellationToken.None);

        _email.Sends.ShouldHaveSingleItem();
        _cache.Content.LastNotifiedVersion.ShouldBeNull();

        _email.Result = true;
        await notifier.NotifyAsync(Update(), CancellationToken.None);

        _email.Sends.Count.ShouldBe(2);
        _cache.Content.LastNotifiedVersion.ShouldBe("3.1.0");
    }

    [Test]
    public async Task ADeliveryFailure_IsLoggedAsWarning_RecordsNothing_AndTheNextCheckSendsAgain()
    {
        _email.Failure = new TransientEmailDeliveryException("The mail server is unreachable");
        var logger = new Mock<ILogger<UpdateNotifier>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var notifier = Notifier("all", logger.Object);

        await Should.NotThrowAsync(() => notifier.NotifyAsync(Update(), CancellationToken.None));

        _cache.Content.LastNotifiedVersion.ShouldBeNull();
        logger.Invocations.Count(i => i.Method.Name == nameof(ILogger.Log) && i.Arguments[0] is LogLevel.Warning).ShouldBe(1);

        _email.Failure = null;
        await notifier.NotifyAsync(Update(), CancellationToken.None);

        _email.Sends.Count.ShouldBe(2);
        _cache.Content.LastNotifiedVersion.ShouldBe("3.1.0");
    }

    [Test]
    public async Task APermanentDeliveryFailure_IsHandledTheSameWay()
    {
        _email.Failure = new EmailDeliveryException("The server refused the login");

        await Should.NotThrowAsync(() => Notifier("all").NotifyAsync(Update(), CancellationToken.None));

        _cache.Content.LastNotifiedVersion.ShouldBeNull();
    }

    [Test]
    public async Task AnyOtherFailureOfTheSend_IsNotSwallowed()
    {
        _email.Failure = new InvalidOperationException("a bug");

        await Should.ThrowAsync<InvalidOperationException>(() => Notifier("all").NotifyAsync(Update(), CancellationToken.None));

        _cache.Content.LastNotifiedVersion.ShouldBeNull();
    }

    [Test]
    public async Task WithoutAdminsToTell_NothingIsSent_NothingIsRecorded_AndAHostThatCanSendsLater()
    {
        _admins.Addresses = [];
        var notifier = Notifier("all");

        await notifier.NotifyAsync(Update(), CancellationToken.None);

        _email.Sends.ShouldBeEmpty();
        _cache.Content.LastNotifiedVersion.ShouldBeNull();

        _admins.Addresses = [Jane];
        await notifier.NotifyAsync(Update(), CancellationToken.None);

        _email.Sends.ShouldHaveSingleItem().To.ShouldBe([Jane]);
        _cache.Content.LastNotifiedVersion.ShouldBe("3.1.0");
    }
}
