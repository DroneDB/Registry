using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Adapters.Archives;
using Registry.Ports.Archives;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class ArchiveExtractToolTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ddb_extracttool_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }
        catch
        {
            // best-effort
        }
    }

    private (ArchiveExtractTool tool, Mock<IUtils> utils) CreateTool(long maxArchiveBytes = 5L * 1024 * 1024 * 1024)
    {
        var extractor = new SharpCompressArchiveExtractor();

        var utils = new Mock<IUtils>();
        utils.Setup(u => u.CheckCurrentUserStorage(It.IsAny<long>())).Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(utils.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var settings = new AppSettings
        {
            ProcessingPlatform = new ProcessingPlatformSettings { MaxArchiveExtractSizeBytes = maxArchiveBytes }
        };

        var tool = new ArchiveExtractTool(
            extractor, scopeFactory, Microsoft.Extensions.Options.Options.Create(settings),
            NullLogger<ArchiveExtractTool>.Instance);

        return (tool, utils);
    }

    private static IHeavyToolValidationContext Ctx(IDDB ddb)
    {
        var ctx = new Mock<IHeavyToolValidationContext>();
        ctx.SetupGet(c => c.Ddb).Returns(ddb);
        ctx.SetupGet(c => c.Logger).Returns(NullLogger.Instance);
        return ctx.Object;
    }

    private static HeavyToolRequest Request(object prms)
    {
        var element = JsonSerializer.SerializeToElement(prms);
        return new HeavyToolRequest("archive-extract", "1", "org", "ds", null, element);
    }

    private Mock<IDDB> MockDdb(string archiveRelPath, string localArchivePath, EntryType type = EntryType.Generic, long size = 0)
    {
        var ddb = new Mock<IDDB>();
        ddb.SetupGet(d => d.DatasetFolderPath).Returns(_dir);
        ddb.Setup(d => d.GetEntry(archiveRelPath)).Returns(new Entry { Path = archiveRelPath, Type = type, Size = size });
        ddb.Setup(d => d.GetLocalPath(archiveRelPath)).Returns(localArchivePath);
        return ddb;
    }

    [Test]
    public async Task ValidateAsync_ValidZip_Passes()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);

        var (tool, utils) = CreateTool();

        await tool.ValidateAsync(Request(new { sourcePath = "flight.zip" }), Ctx(ddb.Object), CancellationToken.None);

        // User storage quota must be checked against the uncompressed size.
        utils.Verify(u => u.CheckCurrentUserStorage(It.Is<long>(v => v > 0)), Times.Once);
    }

    [Test]
    public void ValidateAsync_MissingSource_Throws()
    {
        var ddb = new Mock<IDDB>();
        ddb.SetupGet(d => d.DatasetFolderPath).Returns(_dir);
        ddb.Setup(d => d.GetEntry(It.IsAny<string>())).Returns((Entry)null!);

        var (tool, _) = CreateTool();

        Should.Throw<ArgumentException>(async () =>
            await tool.ValidateAsync(Request(new { sourcePath = "missing.zip" }), Ctx(ddb.Object), CancellationToken.None));
    }

    [Test]
    public void ValidateAsync_UnsupportedFormat_Throws()
    {
        var local = Path.Combine(_dir, "photo.jpg");
        File.WriteAllText(local, "not an archive");
        var ddb = MockDdb("photo.jpg", local);

        var (tool, _) = CreateTool();

        Should.Throw<ArgumentException>(async () =>
            await tool.ValidateAsync(Request(new { sourcePath = "photo.jpg" }), Ctx(ddb.Object), CancellationToken.None));
    }

    [Test]
    public void ValidateAsync_ReservedDestPath_Throws()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);

        var (tool, _) = CreateTool();

        Should.Throw<ArgumentException>(async () =>
            await tool.ValidateAsync(
                Request(new { sourcePath = "flight.zip", destPath = ".ddb/evil" }),
                Ctx(ddb.Object), CancellationToken.None));
    }

    [Test]
    public void ValidateAsync_ArchiveTooLarge_Throws()
    {
        var local = Path.Combine(_dir, "big.zip");
        CreateZip(local, "a.txt", "hello");
        // Report a huge archive size via the index entry.
        var ddb = MockDdb("big.zip", local, size: 10L * 1024 * 1024 * 1024);

        var (tool, _) = CreateTool(maxArchiveBytes: 5L * 1024 * 1024 * 1024);

        var ex = Should.Throw<ArgumentException>(async () =>
            await tool.ValidateAsync(Request(new { sourcePath = "big.zip" }), Ctx(ddb.Object), CancellationToken.None));
        ex.Message.ShouldContain("too large");
    }

    [Test]
    public void ValidateAsync_QuotaExceeded_Propagates()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);

        var (tool, utils) = CreateTool();
        utils.Setup(u => u.CheckCurrentUserStorage(It.IsAny<long>()))
            .ThrowsAsync(new QuotaExceededException("quota exceeded"));

        Should.Throw<QuotaExceededException>(async () =>
            await tool.ValidateAsync(Request(new { sourcePath = "flight.zip" }), Ctx(ddb.Object), CancellationToken.None));
    }

    [Test]
    public void Plan_ReturnsUncompressedEstimate()
    {
        var local = Path.Combine(_dir, "flight.zip");
        CreateZip(local, "a.txt", "hello");
        var ddb = MockDdb("flight.zip", local);

        var (tool, _) = CreateTool();

        var plan = tool.Plan(Request(new { sourcePath = "flight.zip" }), Ctx(ddb.Object));

        plan.QuotaKey.ShouldBe("archive-extract");
        plan.EstimatedOutputBytes.ShouldBe(5); // "hello"
    }

    private static void CreateZip(string path, string entryName, string content)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(entryName);
        using var s = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }
}
