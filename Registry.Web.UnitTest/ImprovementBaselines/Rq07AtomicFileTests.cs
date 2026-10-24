#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-07 (atomic configuration writes): <see cref="AtomicFile"/> writes a temporary file next to the target
/// and renames it over it, so a reader (another container sharing the storage folder) sees the old content
/// or the new one, never a part; a file that is a mount point (Docker's single-file bind mount, where the
/// rename fails with EBUSY) is written in place instead.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq07AtomicFileTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class Rq07AtomicFileTests
{
    private string _folder = null!;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "rq07-atomic", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_folder, true);

    [Test]
    public void Write_ReplacesTheFile_AndLeavesNoTemporaryFile()
    {
        var path = Path.Combine(_folder, "appsettings.json");
        File.WriteAllText(path, "old");

        AtomicFile.WriteAllText(path, "new").ShouldBeTrue();

        File.ReadAllText(path).ShouldBe("new");
        Directory.GetFiles(_folder).ShouldBe([path]);
    }

    /// <summary>A reader never sees a truncated or partial file while the file is rewritten.</summary>
    [Test]
    public async Task ConcurrentReader_SeesTheOldOrTheNewContent_NeverAPart()
    {
        var path = Path.Combine(_folder, "appsettings.json");
        var contents = new[] { new string('a', 512 * 1024), new string('b', 512 * 1024) };
        File.WriteAllText(path, contents[0]);

        using var done = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            var reads = 0;
            while (!done.IsCancellationRequested)
            {
                string text;
                try
                {
                    text = File.ReadAllText(path);
                }
                catch (FileNotFoundException)
                {
                    throw new InvalidOperationException("the file was missing during a write");
                }

                contents.ShouldContain(text, "a reader saw a partial file");
                reads++;
            }

            return reads;
        });

        for (var i = 0; i < 200; i++)
            AtomicFile.WriteAllText(path, contents[(i + 1) % 2]);

        await done.CancelAsync();
        (await reader).ShouldBeGreaterThan(0);
    }

    [Test]
    public void MountPoint_IsWrittenInPlace()
    {
        var path = Path.Combine(_folder, "appsettings.json");
        File.WriteAllText(path, "old");
        var busy = new IOException("Device or resource busy", 16);

        if (OperatingSystem.IsWindows())
            Assert.Ignore("EBUSY is the POSIX errno of a rename onto a mount point");

        AtomicFile.WriteAllText(path, "new", (_, _) => throw busy).ShouldBeFalse();

        File.ReadAllText(path).ShouldBe("new");
        Directory.GetFiles(_folder).ShouldBe([path]);
    }

    [Test]
    public void OtherRenameFailure_Propagates_AndKeepsTheFile()
    {
        var path = Path.Combine(_folder, "appsettings.json");
        File.WriteAllText(path, "old");

        Should.Throw<IOException>(() => AtomicFile.WriteAllText(path, "new", (_, _) => throw new IOException("disk full", 28)));

        File.ReadAllText(path).ShouldBe("old");
        Directory.GetFiles(_folder).ShouldBe([path]);
    }
}
