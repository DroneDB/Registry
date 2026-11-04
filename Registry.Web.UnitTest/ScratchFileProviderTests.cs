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
}
