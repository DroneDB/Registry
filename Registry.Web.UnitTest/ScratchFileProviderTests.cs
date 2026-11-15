using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <see cref="ScratchFileProvider"/> (the configured temp folder and the dataset uploads
/// folder, a new name every time, deleted unless released) and the one best-effort delete,
/// <see cref="FileSystemExtensions.TryDelete"/>.
/// </summary>
[TestFixture]
public class ScratchFileProviderTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "scratch-tests-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Test]
    public void Create_IsANewFileUnderTheConfiguredTempFolder_DeletedOnDispose()
    {
        var scratch = TestScratch.For(Path.Combine(_root, "temp"));

        string path;
        using (var file = scratch.Create("export-", ".tif"))
        {
            path = file.Path;
            Path.GetDirectoryName(path).ShouldBe(Path.Combine(_root, "temp"));
            Path.GetFileName(path).ShouldStartWith("export-");
            Path.GetExtension(path).ShouldBe(".tif");
            File.WriteAllText(path, "x");
        }

        File.Exists(path).ShouldBeFalse();
        scratch.Create("a", ".b").Path.ShouldNotBe(scratch.Create("a", ".b").Path);
    }

    [Test]
    public void CreateForDataset_IsInTheUploadsFolder_AndReleaseKeepsIt()
    {
        var dataset = Path.Combine(_root, "ds");
        var scratch = TestScratch.For(Path.Combine(_root, "temp"));

        var file = scratch.CreateForDataset(dataset);
        Path.GetDirectoryName(file.Path).ShouldBe(Path.Combine(dataset, IDDB.UploadsFolderName));
        Path.GetExtension(file.Path).ShouldBe(".tmp"); // swept by the abandoned-upload cleanup
        File.WriteAllText(file.Path, "x");

        file.Release();
        file.Dispose();
        File.Exists(file.Path).ShouldBeTrue();
    }

    [Test]
    public void Root_WithoutATempPath_FailsWithAClearMessage()
    {
        var scratch = new ScratchFileProvider(Microsoft.Extensions.Options.Options.Create(new StorageOptions()),
            new FileSystem(), NullLogger<ScratchFileProvider>.Instance);

        Should.Throw<InvalidOperationException>(() => scratch.Root).Message.ShouldContain("TempPath");
    }

    [Test]
    public void TryDelete_FileFolderOrNothing_NeverThrows()
    {
        var fs = new FileSystem();
        var folder = Path.Combine(_root, "f");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "a.txt"), "x");
        var file = Path.Combine(_root, "b.txt");
        File.WriteAllText(file, "x");

        fs.TryDelete(file).ShouldBeTrue();
        fs.TryDelete(folder).ShouldBeTrue();
        fs.TryDelete(Path.Combine(_root, "missing")).ShouldBeTrue();
        fs.TryDelete(null).ShouldBeTrue();
        File.Exists(file).ShouldBeFalse();
        Directory.Exists(folder).ShouldBeFalse();
    }

    [Test]
    public void TryDelete_Failure_GoesToTheCallback()
    {
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.Exists("x")).Returns(true);
        fs.Setup(f => f.Delete("x")).Throws(new IOException("busy"));
        Exception? reported = null;

        fs.Object.TryDelete("x", ex => reported = ex).ShouldBeFalse();

        reported.ShouldBeOfType<IOException>();
    }

    // A scratch file handed over to a reader lives as long as its stream: it is read whole after the scratch file is
    // disposed, and gone once the stream is closed, also when the reader stopped early
    [TestCase(true)]
    [TestCase(false)]
    public void OpenForReadingOnce_TheFileIsGoneWhenTheStreamIsClosed(bool readToTheEnd)
    {
        var scratch = TestScratch.For(Path.Combine(_root, "temp"));
        string path;
        Stream stream;
        using (var file = scratch.Create("export-", ".tif"))
        {
            path = file.Path;
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            stream = file.OpenForReadingOnce();
        }

        File.Exists(path).ShouldBeTrue();
        using (stream)
        {
            if (readToTheEnd)
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.ToArray().ShouldBe(new byte[] { 1, 2, 3, 4 });
            }
            else
                stream.ReadByte().ShouldBe(1);
        }

        File.Exists(path).ShouldBeFalse();
    }

    // A file that cannot be opened stays the scratch file's to delete
    [Test]
    public void OpenForReadingOnce_WhenTheFileCannotBeOpened_DisposeStillDeletesIt()
    {
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.OpenReadDeletingOnClose("/tmp/x.tif")).Throws(new IOException("busy"));
        fs.Setup(f => f.Exists("/tmp/x.tif")).Returns(true);

        using (var file = new Registry.Web.Services.Ports.ScratchFile("/tmp/x.tif", fs.Object, (_, _) => { }))
            Should.Throw<IOException>(() => file.OpenForReadingOnce());

        fs.Verify(f => f.Delete("/tmp/x.tif"), Times.Once);
    }
}
