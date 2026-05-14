using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Adapters;
using Shouldly;

namespace Registry.Web.Test.Ogc;

[TestFixture]
public class BuildArtifactResolverTests
{
    private Mock<IFileSystem> _fs = null!;
    private Mock<IDDB> _ddb = null!;
    private BuildArtifactResolver _resolver = null!;
    private const string Hash = "abc123";

    [SetUp]
    public void Setup()
    {
        _fs = new Mock<IFileSystem>();
        _ddb = new Mock<IDDB>();
        // Pass-through GetLocalPath so the relative path under .ddb/build/{hash}/ is preserved
        // up until BuildArtifactResolver applies Path.GetFullPath().
        _ddb.Setup(d => d.GetLocalPath(It.IsAny<string>())).Returns<string>(p => p);
        _resolver = new BuildArtifactResolver(_fs.Object);
    }

    private static string Norm(string p) => p.Replace('\\', '/');

    [Test]
    public void GetMvtDir_BuildsRelativePathUnderDdbBuildHashMvt()
    {
        var result = Norm(_resolver.GetMvtDir(_ddb.Object, Hash));
        result.ShouldEndWith($".ddb/build/{Hash}/mvt");
    }

    [Test]
    public void GetMvtMetadataPath_EndsWithMetadataJson()
    {
        var result = Norm(_resolver.GetMvtMetadataPath(_ddb.Object, Hash));
        result.ShouldEndWith($".ddb/build/{Hash}/mvt/metadata.json");
    }

    [Test]
    public void GetMvtTilePath_IncludesZxyAndPbfExtension()
    {
        var result = Norm(_resolver.GetMvtTilePath(_ddb.Object, Hash, 7, 12, 34));
        result.ShouldEndWith($".ddb/build/{Hash}/mvt/7/12/34.pbf");
    }

    [Test]
    public void GetCogPath_EndsWithCogTif()
    {
        var result = Norm(_resolver.GetCogPath(_ddb.Object, Hash));
        result.ShouldEndWith($".ddb/build/{Hash}/cog/cog.tif");
    }

    [Test]
    public void GetVectorQueryPath_EndsWithSourceGpkg()
    {
        var result = Norm(_resolver.GetVectorQueryPath(_ddb.Object, Hash));
        result.ShouldEndWith($".ddb/build/{Hash}/vec/source.gpkg");
    }

    [Test]
    public void GetMvtDir_ReturnsAbsolutePath()
    {
        var result = _resolver.GetMvtDir(_ddb.Object, Hash);
        System.IO.Path.IsPathRooted(result).ShouldBeTrue();
    }

    [Test]
    public void ArtifactExists_ReturnsFalseForNullOrEmpty()
    {
        _resolver.ArtifactExists(null!).ShouldBeFalse();
        _resolver.ArtifactExists(string.Empty).ShouldBeFalse();
        _fs.Verify(f => f.Exists(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public void ArtifactExists_ProxiesToFileSystem_WhenPathIsValid()
    {
        _fs.Setup(f => f.Exists("C:/some/file.gpkg")).Returns(true);
        _resolver.ArtifactExists("C:/some/file.gpkg").ShouldBeTrue();
        _fs.Verify(f => f.Exists("C:/some/file.gpkg"), Times.Once);
    }
}
