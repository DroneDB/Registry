#nullable enable
using Registry.Web.Test.Support;
using System;
using System.Threading;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Web.Services.Adapters;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>P4: "Set coordinate system" writes the drawing's .prj through the normal add.</summary>
[TestFixture]
public class CadCrsManagerTests
{
    private const string Org = "org";
    private const string Ds = "ds";

    private Mock<IAuthManager> _auth = null!;
    private Mock<IDdbWrapper> _wrapper = null!;
    private Mock<IObjectsManager> _objects = null!;
    private Mock<IDDB> _ddb = null!;
    private string? _written;

    [SetUp]
    public void SetUp()
    {
        _auth = new Mock<IAuthManager>();
        _auth.Setup(a => a.RequestAccess(It.IsAny<Dataset>(), AccessType.Write)).ReturnsAsync(true);
        _wrapper = new Mock<IDdbWrapper>(MockBehavior.Strict);
        _wrapper.Setup(w => w.MakeCadPrj("EPSG:32632")).Returns("PROJCS[\"WGS_1984_UTM_Zone_32N\"]");
        _wrapper.Setup(w => w.MakeCadPrj("nonsense")).Throws(new ArgumentException("Not a coordinate system: nonsense"));
        _ddb = new Mock<IDDB>();
        _ddb.Setup(d => d.GetEntry("site/plan.dwg")).Returns(new Registry.Ports.DroneDB.Entry { Path = "site/plan.dwg", Type = EntryType.Vector });
        _objects = new Mock<IObjectsManager>();
        _objects.Setup(o => o.AddNew(Org, Ds, It.IsAny<string>(), It.IsAny<Stream>()))
            .Returns((string _, string _, string path, Stream s) =>
            {
                _written = new StreamReader(s, Encoding.UTF8).ReadToEnd();
                return Task.FromResult(new EntryDto { Path = path });
            });
        _written = null;
    }

    private CadCrsManager Create()
    {
        var dataset = new Dataset { Slug = Ds, InternalRef = Guid.NewGuid() };
        var utils = new Mock<IDatasetResolver>();
        utils.Setup(u => u.GetDatasetAsync(Org, Ds, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(dataset);
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get(Org, dataset.InternalRef)).Returns(_ddb.Object);
        return new CadCrsManager(new DatasetAccessResolver(utils.Object, _auth.Object, ddbManager.Object, TestPaths.Policy), _wrapper.Object, _objects.Object,
            NullLogger<CadCrsManager>.Instance);
    }

    [Test]
    public async Task WritesThePrjNextToTheDrawing()
    {
        var prj = await Create().SetAsync(Org, Ds, "site/plan.dwg", "EPSG:32632");

        prj.Path.ShouldBe("site/plan.prj");
        _written.ShouldBe("PROJCS[\"WGS_1984_UTM_Zone_32N\"]");
    }

    [Test]
    public async Task InvalidCoordinateSystem_IsBadRequest()
    {
        await Should.ThrowAsync<BadRequestException>(() => Create().SetAsync(Org, Ds, "site/plan.dwg", "nonsense"));
        _objects.Verify(o => o.AddNew(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>()),
            Times.Never);
    }

    [TestCase("site/photo.jpg")]
    [TestCase("")]
    public async Task NotADrawing_IsBadRequest(string path)
    {
        await Should.ThrowAsync<BadRequestException>(() => Create().SetAsync(Org, Ds, path, "EPSG:32632"));
        _wrapper.VerifyNoOtherCalls();
    }

    [Test]
    public async Task MissingDrawing_IsNotFound()
    {
        await Should.ThrowAsync<NotFoundException>(() => Create().SetAsync(Org, Ds, "other.dxf", "EPSG:32632"));
    }

    [Test]
    public async Task WithoutWriteAccess_IsForbidden()
    {
        _auth.Setup(a => a.RequestAccess(It.IsAny<Dataset>(), AccessType.Write)).ReturnsAsync(false);
        await Should.ThrowAsync<ForbiddenException>(() => Create().SetAsync(Org, Ds, "site/plan.dwg", "EPSG:32632"));
        _objects.VerifyNoOtherCalls();
    }
}
