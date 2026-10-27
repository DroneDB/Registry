#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.Email;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Users;
using Registry.Web.Test.Support;

namespace Registry.Web.Test.BuildNotifications;

/// <summary>
/// The recorder, the digest job and the cleanup over one real (SQLite file) outbox, each built on its own context as a
/// job scope would be, with the ports around them recorded: the scheduled digests, the e-mails queued and the models
/// composed. The file database lets two digest runs work at the same time, as on two hosts.
/// </summary>
internal sealed class NotificationHarness : IDisposable
{
    public const string Org = "org";
    public const string Ds = "ds";

    private readonly TestDb<RegistryContext> _db = TestDb.CreateFile<RegistryContext>();

    public FakeTime Time { get; } = new(new DateTime(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc));

    public Dictionary<string, BuildNotificationLevel> Levels { get; } = new();

    public HashSet<string> WithoutAddress { get; } = [];

    public BuildNotificationOptions Options { get; } = new();

    public Mock<IEmailService> Email { get; } = new();

    public Mock<IBuildRequesterResolver> Requesters { get; } = new();

    /// <summary>The digests scheduled: user and delay.</summary>
    public ConcurrentQueue<(string UserId, TimeSpan Delay)> Scheduled { get; } = new();

    /// <summary>The models of the e-mails queued, with their recipient.</summary>
    public ConcurrentQueue<(EmailAddress To, BuildNotificationModel Model)> Sent { get; } = new();

    /// <summary>Runs inside the queueing of an e-mail, before it is recorded as sent (to act while a digest sends).</summary>
    public Func<Task>? WhileQueueing { get; set; }

    private readonly Mock<IBackgroundJobsProcessor> _jobs = new();
    private readonly Mock<IBuildNotificationLevels> _levels = new();
    private readonly Mock<IUserRecipients> _recipients = new();
    private readonly Mock<IBuildNotificationComposer> _composer = new();

    public NotificationHarness()
    {
        Email.SetupGet(e => e.IsEnabled).Returns(true);
        Email.Setup(e => e.QueueAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns<EmailMessage, CancellationToken>(async (message, _) =>
            {
                if (WhileQueueing != null)
                    await WhileQueueing();
                Sent.Enqueue((message.To[0], _models[message]));
                return "email-job";
            });

        // Each line goes to the user the job carries, for the request that job is
        Requesters.Setup(r => r.ResolveAsync(It.IsAny<BuildReport>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BuildReport report, CancellationToken _) =>
                report.Request.UserId == null ? null : new BuildRequester(report.Request.UserId, report.Request.JobId));

        _levels.Setup(l => l.OfAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string userId, CancellationToken _) => Levels.GetValueOrDefault(userId));
        _recipients.Setup(r => r.FindAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string userId, CancellationToken _) =>
                WithoutAddress.Contains(userId) ? null : new EmailAddress($"{userId}@example.com", userId));
        _composer.Setup(c => c.ComposeAsync(It.IsAny<EmailAddress>(), It.IsAny<BuildNotificationModel>()))
            .ReturnsAsync((EmailAddress to, BuildNotificationModel model) =>
            {
                var message = new EmailMessage([to], model.Summary, model.Summary);
                _models[message] = model;
                return message;
            });

        _jobs.Setup(j => j.Schedule(It.IsAny<Expression<Func<BuildNotificationDigestJob, Task>>>(), It.IsAny<TimeSpan>()))
            .Returns<Expression<Func<BuildNotificationDigestJob, Task>>, TimeSpan>((call, delay) =>
            {
                Scheduled.Enqueue(((string)Job.FromExpression(call).Args[0], delay));
                return "digest-" + Scheduled.Count;
            });
    }

    private readonly ConcurrentDictionary<EmailMessage, BuildNotificationModel> _models =
        new(ReferenceEqualityComparer.Instance);

    private BuildNotificationOutbox Outbox() => new(_db.NewContext(), Time);

    private BuildNotificationDigests Digests() =>
        new(_jobs.Object, Microsoft.Extensions.Options.Options.Create(Options), Time,
            NullLogger<BuildNotificationDigests>.Instance);

    /// <summary>Whether the recorder finds who asked in the job index (the real resolver) instead of the job's user.</summary>
    public bool RequestersFromJobIndex { get; set; }

    public BuildNotificationRecorder Recorder() =>
        new(Email.Object, RequestersFromJobIndex ? new BuildRequesterResolver(_db.NewContext()) : Requesters.Object,
            _levels.Object, Outbox(), Digests(), NullLogger<BuildNotificationRecorder>.Instance);

    /// <summary>The reporter of the ends of builds; only the path that needs no dataset (the retries gave up) works.</summary>
    public FinishedBuildReporter Reporter()
    {
        var db = _db.NewContext();
        return new FinishedBuildReporter(Email.Object, new Services.Jobs.DatasetSweep(db), Mock.Of<Registry.Ports.IDdbManager>(),
            Mock.Of<IBuildOutcomeClassifier>(), Recorder(), new BuildNotificationOutbox(db, Time),
            NullLogger<FinishedBuildReporter>.Instance);
    }

    /// <summary>Adds the job index row of a build of <paramref name="path"/> that ended <paramref name="minute"/> minutes after now.</summary>
    public void IndexedBuild(string jobId, string userId, string state, int minute, string path, string hash)
    {
        var at = Time.Now.AddMinutes(minute);
        Change(db => db.JobIndices.Add(new JobIndex
        {
            JobId = jobId, OrgSlug = Org, DsSlug = Ds, Path = path, Hash = hash, UserId = userId, CurrentState = state,
            CreatedAtUtc = at, FailedAtUtc = state == "Failed" ? at.AddSeconds(30) : null,
            SucceededAtUtc = state == "Succeeded" ? at.AddSeconds(30) : null
        }));
    }

    public BuildNotificationDigestJob DigestJob() =>
        new(Outbox(), _levels.Object, _recipients.Object, _composer.Object, Email.Object, Digests(),
            NullLogger<BuildNotificationDigestJob>.Instance);

    public BuildNotificationCleanup Cleanup() => new(Outbox(), Digests(), Time);

    public static BuildReport Report(string userId, string path, BuildOutcome outcome, string jobId, string? hash = null) =>
        new(Org, Ds, path, hash ?? path + "-hash", outcome, outcome == BuildOutcome.Failed ? "it broke" : null,
            new BuildRequest(userId, jobId));

    public Task Record(string userId, string path, BuildOutcome outcome, string jobId, string? hash = null) =>
        Recorder().RecordAsync(Report(userId, path, outcome, jobId, hash), CancellationToken.None);

    public List<BuildNotification> Rows()
    {
        using var db = _db.NewContext();
        return db.BuildNotifications.AsNoTracking().OrderBy(n => n.Id).ToList();
    }

    public void Change(Action<RegistryContext> change)
    {
        using var db = _db.NewContext();
        change(db);
        db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();
}
