using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.BuildNotifications;

/// <summary>
/// FR-PRC-24, OBS-003, SEC-001: the reason of a build-failure e-mail is the text of <see cref="JobLogText.UserMessage(Exception?, string?)"/>:
/// the e-mail leaves the server, so it has no server path and no build hash, and a known GDAL message is said in
/// plain words.
/// </summary>
[TestFixture]
public class BuildReportTests
{
    private const string Folder = "/mnt/data/datasets/org/0b9c4a52-3f5e-4c1e-9a65-1f0e2d3c4b5a";
    private const string Hash = "7a9f3c2e1d0b4a5968778695a4b3c2d1e0f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5";

    [Test]
    public void ShortError_OfALoggedMessage_HasNoServerPathAndNoHash()
    {
        var error = BuildReport.ShortError(
            $"DdbException: Cannot open {Folder}/.ddb/build/{Hash}/ept/ept.json: cannot open (and {Folder}/a.las)",
            Folder);

        error.ShouldBe("DdbException: Cannot open [build folder]: cannot open (and [dataset]/a.las)");
        error!.ShouldNotContain("/mnt/");
        error!.ShouldNotContain(Hash);
        BuildReport.ShortError($"Cannot open {Folder}/a.las", null).ShouldBe("Cannot open [server path]");
    }

    [Test]
    public void ShortError_SaysAKnownMessageInPlainWords() =>
        BuildReport.ShortError($"gdal_translate failed to create output COG: {Folder}/.ddb/build/{Hash}/cog/cog.tif",
            Folder).ShouldBe(JobLogText.DamagedRasterMessage);

    [Test]
    public void ShortError_IsCutToTheColumn_AndBlankIsNone()
    {
        BuildReport.ShortError(new string('x', 3000), Folder)!.Length.ShouldBe(BuildNotification.ErrorMaxLength);
        BuildReport.ShortError("  ", Folder).ShouldBeNull();
    }

    [Test]
    public async Task Reporter_AFailedBuild_ReportsTheUserMessage()
    {
        using var db = TestDb.CreateContext<RegistryContext>();
        var org = Seed.Organization(db, "org");
        var dataset = Seed.Dataset(db, org, "ds");
        db.SaveChanges();

        var ddb = new Mock<IDDB>();
        ddb.SetupGet(d => d.DatasetFolderPath).Returns(Folder);
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Open("org", dataset.InternalRef)).Returns(ddb.Object);
        var classifier = new Mock<IBuildOutcomeClassifier>();
        classifier.Setup(c => c.Classify(ddb.Object, "ortho.tif", "h", false))
            .Returns(new ClassifiedBuild(BuildOutcome.Failed, "h"));
        var email = new Mock<IEmailService>();
        email.SetupGet(e => e.IsEnabled).Returns(true);
        BuildReport? reported = null;
        var recorder = new Mock<IBuildNotificationRecorder>();
        recorder.Setup(r => r.RecordAsync(It.IsAny<BuildReport>(), It.IsAny<CancellationToken>()))
            .Callback<BuildReport, CancellationToken>((r, _) => reported = r)
            .Returns(Task.CompletedTask);
        var reporter = new FinishedBuildReporter(email.Object, new DatasetSweep(db), ddbManager.Object,
            classifier.Object, recorder.Object, new BuildNotificationOutbox(db, new FakeTime(DateTime.UtcNow)),
            NullLogger<FinishedBuildReporter>.Instance);

        await reporter.ReportAsync(new FinishedBuildJob("j1", "org", "ds", "ortho.tif", "h", "user",
            new InvalidOperationException("Cannot build 'ortho.tif'",
                new DdbException($"'{Folder}/ortho.tif' not recognized as a supported file format."))),
            CancellationToken.None);

        reported.ShouldNotBeNull();
        reported.Path.ShouldBe("ortho.tif");
        reported.Error.ShouldBe("The file is damaged or not a valid GeoTIFF");
    }
}
