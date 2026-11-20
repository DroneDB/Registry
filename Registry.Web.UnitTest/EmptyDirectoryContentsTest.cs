using System.IO;
using System.Linq;
using NUnit.Framework;
using Registry.Common;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class EmptyDirectoryContentsTest
{
    private string _tempRoot;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ddb_emptydir_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
    }

    [Test]
    public void EmptyDirectoryContents_RemovesFilesAndSubdirectories_PreservesRoot()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "a.txt"), "1");
        File.WriteAllText(Path.Combine(_tempRoot, "b.txt"), "2");
        var sub = Path.Combine(_tempRoot, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "c.txt"), "3");
        var subSub = Path.Combine(sub, "deep");
        Directory.CreateDirectory(subSub);
        File.WriteAllText(Path.Combine(subSub, "d.txt"), "4");

        CommonUtils.EmptyDirectoryContents(_tempRoot);

        Directory.Exists(_tempRoot).ShouldBeTrue();
        Directory.EnumerateFileSystemEntries(_tempRoot).Any().ShouldBeFalse();
    }

    [Test]
    public void EmptyDirectoryContents_OnEmptyDirectory_IsNoOp()
    {
        Should.NotThrow(() => CommonUtils.EmptyDirectoryContents(_tempRoot));
        Directory.Exists(_tempRoot).ShouldBeTrue();
    }

    [Test]
    public void EmptyDirectoryContents_OnMissingPath_IsNoOp()
    {
        var missing = Path.Combine(_tempRoot, "does-not-exist");
        Should.NotThrow(() => CommonUtils.EmptyDirectoryContents(missing));
    }
}
