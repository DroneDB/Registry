using System;
using System.IO;
using Moq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Shouldly;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Test;

/// <summary>
/// <see cref="DDB.GetEntry"/> / <see cref="DDB.EntryExists"/> do one exact native
/// lookup (DDBGet). They used to list the path recursively (a folder marshalled its whole
/// subtree) and then list the parent: 2 native calls for folders and missing paths.
/// The wrapper mock is strict, so any listing call fails the test.
/// </summary>
[TestFixture]
public class DdbGetEntryTests : TestBase
{
    private string _root = null!;
    private Mock<IDdbWrapper> _wrapper = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "reg01-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _wrapper = new Mock<IDdbWrapper>(MockBehavior.Strict);
        _wrapper.Setup(w => w.GetEntry(_root, It.IsAny<string>()))
            .Returns((string _, string path) => path switch
            {
                "dir" => new Entry { Path = "dir", Type = EntryType.Directory },
                "dir/IMG_0001.JPG" => new Entry { Path = "dir/IMG_0001.JPG", Type = EntryType.GeoImage, Hash = "h1" },
                _ => null
            });
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    private DDB CreateDdb() => new(_root, _wrapper.Object);

    [Test]
    public void GetEntry_File_UsesSingleExactLookup()
    {
        var e = CreateDdb().GetEntry("dir/IMG_0001.JPG");

        e.ShouldNotBeNull();
        e.Hash.ShouldBe("h1");
        _wrapper.Verify(w => w.GetEntry(_root, "dir/IMG_0001.JPG"), Times.Once);
        _wrapper.VerifyNoOtherCalls();
    }

    [Test]
    public void GetEntry_Folder_SingleCallWithoutSubtree()
    {
        var e = CreateDdb().GetEntry("dir");

        e.ShouldNotBeNull();
        e.Path.ShouldBe("dir");
        e.Type.ShouldBe(EntryType.Directory);
        _wrapper.Verify(w => w.GetEntry(_root, "dir"), Times.Once);
        _wrapper.VerifyNoOtherCalls();
    }

    [Test]
    public void GetEntry_Missing_SingleCall()
    {
        CreateDdb().GetEntry("dir/missing.JPG").ShouldBeNull();

        _wrapper.Verify(w => w.GetEntry(_root, "dir/missing.JPG"), Times.Once);
        _wrapper.VerifyNoOtherCalls();
    }

    [Test]
    public void EntryExists_Folder_SingleCall()
    {
        CreateDdb().EntryExists("dir").ShouldBeTrue();

        _wrapper.Verify(w => w.GetEntry(_root, "dir"), Times.Once);
        _wrapper.VerifyNoOtherCalls();
    }

    [TestCase("./dir/IMG_0001.JPG")]
    [TestCase("dir/../dir/IMG_0001.JPG")]
    [TestCase("dir//IMG_0001.JPG")]
    public void GetEntry_ResolvesThePathLikeListing(string path)
    {
        CreateDdb().GetEntry(path).ShouldNotBeNull();

        _wrapper.Verify(w => w.GetEntry(_root, "dir/IMG_0001.JPG"), Times.Once);
    }

    [Test]
    public void GetEntry_TrailingSlash_FindsFolder()
    {
        CreateDdb().GetEntry("dir/").ShouldNotBeNull();

        _wrapper.Verify(w => w.GetEntry(_root, "dir"), Times.Once);
    }

    [TestCase("")]
    [TestCase(".")]
    [TestCase("..")]
    [TestCase("../other/file.jpg")]
    [TestCase("/etc/passwd")]
    public void GetEntry_RootOrOutsideDataset_IsNullWithoutNativeCall(string path)
    {
        CreateDdb().GetEntry(path).ShouldBeNull();

        _wrapper.VerifyNoOtherCalls();
    }

    [Test]
    public void GetEntry_NativeFailure_IsWrapped()
    {
        _wrapper.Setup(w => w.GetEntry(_root, "broken.jpg")).Throws(new DdbException("boom"));

        Should.Throw<InvalidOperationException>(() => CreateDdb().GetEntry("broken.jpg"));
    }

    [Test]
    public void GetEntry_Busy_IsNotWrapped()
    {
        // Callers retry on DdbBusyException (503 + Retry-After)
        _wrapper.Setup(w => w.GetEntry(_root, "busy.jpg")).Throws(new DdbBusyException("busy"));

        Should.Throw<DdbBusyException>(() => CreateDdb().GetEntry("busy.jpg"));
    }
}
