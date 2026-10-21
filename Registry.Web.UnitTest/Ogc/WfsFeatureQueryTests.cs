#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities.Ogc;
using Shouldly;

namespace Registry.Web.Test.Ogc;

/// <summary>
/// REG-11: WFS GetFeatureById and RESOURCEID read the requested features by id (one primary-key
/// query, no scan of every layer at 10 000 features), plain GetFeature reports the real
/// numberMatched, and OGC API Features /items/{id} reads one feature.
/// </summary>
[TestFixture]
public class WfsFeatureQueryTests
{
    private const string Org = "org";
    private const string Ds = "ds";
    private const string Gpkg = "/data/build/h1/vec/source.gpkg";
    private const string Gml = "application/gml+xml; version=3.2";

    private static readonly OgcLayerDto Layer = new()
    {
        Name = "A010101.shp", Title = "A010101.shp", EntryType = EntryType.Vector, EntryPath = "A010101.shp",
        EntryHash = "h1", InnerLayerName = "A010101"
    };

    private Mock<IDdbWrapper> _wrapper = null!;
    private Mock<IOgcLayerCatalog> _catalog = null!;
    private Mock<IUtils> _utils = null!;
    private Mock<IAuthManager> _auth = null!;
    private Mock<IDdbManager> _ddbManager = null!;
    private Mock<IBuildArtifactResolver> _artifacts = null!;
    private string _polygons = null!;

    [SetUp]
    public void SetUp()
    {
        _polygons = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Data", "Wfs",
            "polygons.input.gml"));

        var dataset = new Dataset { Slug = Ds, InternalRef = Guid.NewGuid() };
        _utils = new Mock<IUtils>();
        _utils.Setup(u => u.GetDataset(Org, Ds, It.IsAny<bool>(), It.IsAny<bool>())).Returns(dataset);
        _auth = new Mock<IAuthManager>();
        _auth.Setup(a => a.RequestAccess(dataset, AccessType.Read)).ReturnsAsync(true);
        _ddbManager = new Mock<IDdbManager>();
        _ddbManager.Setup(m => m.Get(Org, dataset.InternalRef)).Returns(Mock.Of<IDDB>());
        _artifacts = new Mock<IBuildArtifactResolver>();
        _artifacts.Setup(a => a.GetVectorQueryPath(It.IsAny<IDDB>(), "h1")).Returns(Gpkg);
        _artifacts.Setup(a => a.ArtifactExists(Gpkg)).Returns(true);

        _catalog = new Mock<IOgcLayerCatalog>();
        _catalog.Setup(c => c.GetLayersAsync(Org, Ds, It.IsAny<string?>(), It.IsAny<bool>())).ReturnsAsync([Layer]);
        _catalog.Setup(c => c.ResolveAsync(Org, Ds, Layer.Name, It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(Layer);

        _wrapper = new Mock<IDdbWrapper>(MockBehavior.Strict);
        _wrapper.Setup(w => w.QueryVector(Gpkg, It.IsAny<VectorQuery>())).Returns(_polygons);
        _wrapper.Setup(w => w.CountVector(Gpkg, It.IsAny<VectorQuery>())).Returns(25036);
        _wrapper.Setup(w => w.DescribeVector(Gpkg, "A010101")).Returns(
            "{\"layers\":[{\"name\":\"A010101\",\"fields\":[{\"name\":\"FEATURE_ID\",\"type\":\"String\"}," +
            "{\"name\":\"STRATO\",\"type\":\"String\"},{\"name\":\"CLASSE\",\"type\":\"String\"}]}]}");
    }

    private const string Json = "application/json";

    private static string Filter(string predicate) =>
        "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\" xmlns:gml=\"http://www.opengis.net/gml/3.2\">" +
        predicate + "</fes:Filter>";

    private static string EqualTo(string property, string literal) =>
        $"<fes:PropertyIsEqualTo><fes:ValueReference>{property}</fes:ValueReference>" +
        $"<fes:Literal>{literal}</fes:Literal></fes:PropertyIsEqualTo>";

    private IDistributedCache Cache() =>
        new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));

    private WfsManager CreateWfs() => new(_utils.Object, _auth.Object, _ddbManager.Object, _artifacts.Object,
        _wrapper.Object, _catalog.Object, Cache(), new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
        NullLogger<WfsManager>.Instance);

    private OgcApiFeaturesManager CreateFeatures() => new(_utils.Object, _auth.Object, _ddbManager.Object,
        _artifacts.Object, _wrapper.Object, _catalog.Object, Cache(),
        new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, NullLogger<OgcApiFeaturesManager>.Instance);

    private static bool Fids(VectorQuery q, params long[] fids) => q.Fids != null && q.Fids.SequenceEqual(fids);

    [Test]
    public async Task GetFeatureById_ReadsOneFeatureByItsFid()
    {
        var feature = await CreateWfs().GetFeatureByIdAsync(Org, Ds, "A010101.shp.2", Gml);

        var root = XDocument.Parse(feature).Root!;
        root.Name.ShouldBe(XName.Get("A010101.shp", OgcNamespaces.Ddb));
        _wrapper.Verify(w => w.QueryVector(Gpkg, It.Is<VectorQuery>(q =>
            q.LayerName == "A010101" && Fids(q, 2) && q.MaxFeatures == 1 && q.Bbox == null)), Times.Once);
        _wrapper.VerifyNoOtherCalls();
    }

    [TestCase("other_layer.2")]
    [TestCase("A010101.shp")]
    [TestCase("A010101.shp.x")]
    public async Task GetFeatureById_UnknownId_Is404WithoutReadingFeatures(string id)
    {
        var ex = await Should.ThrowAsync<OgcException>(() => CreateWfs().GetFeatureByIdAsync(Org, Ds, id, Gml));

        ex.HttpStatusCode.ShouldBe(404);
        _wrapper.VerifyNoOtherCalls();
    }

    [Test]
    public async Task GetFeatureById_MissingFeature_Is404()
    {
        _wrapper.Setup(w => w.QueryVector(Gpkg, It.IsAny<VectorQuery>()))
            .Returns(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Data", "Wfs", "empty.input.gml")));

        var ex = await Should.ThrowAsync<OgcException>(() =>
            CreateWfs().GetFeatureByIdAsync(Org, Ds, "A010101.shp.999999", Gml));

        ex.HttpStatusCode.ShouldBe(404);
    }

    [Test]
    public async Task GetFeature_ResourceId_ReadsOnlyThoseFeatures()
    {
        await CreateWfs().GetFeatureAsync(Org, Ds, Layer.Name, null, null, 10, 0, Gml,
            resourceId: "A010101.shp.7, A010101.shp.9,other.3,A010101.shp.7");

        _wrapper.Verify(w => w.QueryVector(Gpkg, It.Is<VectorQuery>(q =>
            Fids(q, 7, 9) && q.MaxFeatures == 10 && q.StartIndex == 0)), Times.Once);
        _wrapper.Verify(w => w.QueryVector(Gpkg, It.Is<VectorQuery>(q => q.MaxFeatures == 10000)), Times.Never);
    }

    [Test]
    public async Task GetFeature_ResourceIdOfAnotherType_IsEmptyWithoutAQuery()
    {
        var fc = await CreateWfs().GetFeatureAsync(Org, Ds, Layer.Name, null, null, 10, 0, Gml,
            resourceId: "other.3");

        var root = XDocument.Parse(fc).Root!;
        root.Name.ShouldBe(XName.Get("FeatureCollection", OgcNamespaces.Wfs));
        root.Attribute("numberReturned")!.Value.ShouldBe("0");
        _wrapper.VerifyNoOtherCalls();
    }

    [Test]
    public async Task GetFeature_Page_ReportsTheTotalAsNumberMatched()
    {
        double[] bbox = [9.15, 45.40, 9.16, 45.41];
        var fc = await CreateWfs().GetFeatureAsync(Org, Ds, Layer.Name, bbox, "EPSG:4326", 2, 4, Gml);

        var root = XDocument.Parse(fc).Root!;
        root.Attribute("numberReturned")!.Value.ShouldBe("2");
        root.Attribute("numberMatched")!.Value.ShouldBe("25036");
        _wrapper.Verify(w => w.QueryVector(Gpkg, It.Is<VectorQuery>(q =>
            q.Bbox == bbox && q.MaxFeatures == 2 && q.StartIndex == 4 && q.Fids == null)), Times.Once);
        _wrapper.Verify(w => w.CountVector(Gpkg, It.Is<VectorQuery>(q => q.Bbox == bbox && q.Fids == null)), Times.Once);
    }

    [Test]
    public async Task GetFeature_TranslatedFilter_IsEvaluatedPagedAndCountedByDroneDb()
    {
        var fc = await CreateWfs().GetFeatureAsync(Org, Ds, Layer.Name, null, null, 5, 10, Gml,
            filterXml: Filter(EqualTo("ddb:FEATURE_ID", "000000000002")));

        const string where = "\"FEATURE_ID\" = '000000000002'";
        XDocument.Parse(fc).Root!.Attribute("numberMatched")!.Value.ShouldBe("25036");
        _wrapper.Verify(w => w.QueryVector(Gpkg, It.Is<VectorQuery>(q =>
            q.Where == where && q.MaxFeatures == 5 && q.StartIndex == 10 && q.OutputFormat == "gml")), Times.Once);
        _wrapper.Verify(w => w.CountVector(Gpkg, It.Is<VectorQuery>(q => q.Where == where)), Times.Once);
        _wrapper.Verify(w => w.QueryVector(Gpkg, It.Is<VectorQuery>(q => q.MaxFeatures == 10000)), Times.Never);
    }

    [Test]
    public async Task GetFeature_TranslatedFilter_AppliesToGeoJson()
    {
        await CreateWfs().GetFeatureAsync(Org, Ds, Layer.Name, null, null, 5, 0, Json,
            filterXml: Filter(EqualTo("STRATO", "01")));

        _wrapper.Verify(w => w.QueryVector(Gpkg, It.Is<VectorQuery>(q =>
            q.Where == "\"STRATO\" = '01'" && q.OutputFormat == "geojson")), Times.Once);
    }

    [Test]
    public async Task GetFeature_BboxInFilter_BecomesTheQueryBbox()
    {
        const string bbox = "<fes:BBOX><fes:ValueReference>geom</fes:ValueReference>" +
                            "<gml:Envelope srsName=\"urn:ogc:def:crs:EPSG::4326\"><gml:lowerCorner>45.4 9.15</gml:lowerCorner>" +
                            "<gml:upperCorner>45.42 9.16</gml:upperCorner></gml:Envelope></fes:BBOX>";

        await CreateWfs().GetFeatureAsync(Org, Ds, Layer.Name, null, null, 5, 0, Gml,
            filterXml: Filter("<fes:And>" + bbox + EqualTo("CLASSE", "01") + "</fes:And>"));

        _wrapper.Verify(w => w.QueryVector(Gpkg, It.Is<VectorQuery>(q =>
            q.Bbox != null && q.Bbox.SequenceEqual(new[] { 9.15, 45.4, 9.16, 45.42 }) &&
            q.Where == "\"CLASSE\" = '01'")), Times.Once);
    }

    [Test]
    public async Task GetFeature_UntranslatableFilter_IsEvaluatedHereOnAllSelectedFeatures()
    {
        var fc = await CreateWfs().GetFeatureAsync(Org, Ds, Layer.Name, null, null, 1, 0, Gml,
            filterXml: Filter(EqualTo("ddbSeq", "1")));

        var root = XDocument.Parse(fc).Root!;
        root.Attribute("numberReturned")!.Value.ShouldBe("1");
        root.Attribute("numberMatched")!.Value.ShouldBe("1");
        _wrapper.Verify(w => w.QueryVector(Gpkg, It.Is<VectorQuery>(q =>
            q.MaxFeatures == 10000 && q.StartIndex == 0 && q.Where == null)), Times.Once);
        _wrapper.Verify(w => w.CountVector(It.IsAny<string>(), It.IsAny<VectorQuery>()), Times.Never);
    }

    // The filter used to be ignored for every format but GML, and count/startIndex with it
    [Test]
    public async Task GetFeature_UntranslatableFilter_IsPagedAndKeepsDdbSeq()
    {
        var fc = await CreateWfs().GetFeatureAsync(Org, Ds, Layer.Name, null, null, 1, 1, Gml,
            filterXml: Filter("<fes:PropertyIsGreaterThanOrEqualTo><fes:ValueReference>ddbSeq</fes:ValueReference>" +
                              "<fes:Literal>0</fes:Literal></fes:PropertyIsGreaterThanOrEqualTo>"));

        var root = XDocument.Parse(fc).Root!;
        root.Attribute("numberMatched")!.Value.ShouldBe("2");
        root.Attribute("numberReturned")!.Value.ShouldBe("1");
        var seq = root.Descendants(XName.Get("ddbSeq", OgcNamespaces.Ddb)).Single();
        seq.Value.ShouldBe("1", "the second feature keeps its position in the unfiltered collection");
    }

    [Test]
    public async Task GetFeature_UntranslatableFilter_AppliesToGeoJson()
    {
        var geoJson = await CreateWfs().GetFeatureAsync(Org, Ds, Layer.Name, null, null, 10, 0, Json,
            filterXml: Filter(EqualTo("ddbSeq", "1")));

        geoJson.ShouldBe(_polygons, "the page of matches is read again in the requested format");
        _wrapper.Verify(w => w.QueryVector(Gpkg, It.Is<VectorQuery>(q =>
            q.MaxFeatures == 10000 && q.OutputFormat == "gml")), Times.Once);
        _wrapper.Verify(w => w.QueryVector(Gpkg, It.Is<VectorQuery>(q =>
            Fids(q, 2) && q.OutputFormat == "geojson" && q.StartIndex == 0)), Times.Once);
    }

    [Test]
    public async Task GetFeature_FilterDroneDbRejects_IsEvaluatedHere()
    {
        _wrapper.Setup(w => w.QueryVector(Gpkg, It.Is<VectorQuery>(q => q.Where != null)))
            .Throws(new Registry.Ports.DroneDB.DdbException("no such column"));

        var fc = await CreateWfs().GetFeatureAsync(Org, Ds, Layer.Name, null, null, 10, 0, Gml,
            filterXml: Filter(EqualTo("FEATURE_ID", "000000000001")));

        var root = XDocument.Parse(fc).Root!;
        root.Attribute("numberMatched")!.Value.ShouldBe("1");
        _wrapper.Verify(w => w.QueryVector(Gpkg, It.Is<VectorQuery>(q => q.MaxFeatures == 10000)), Times.Once);
    }

    [Test]
    public async Task OgcApiItem_ReadsOneFeatureByItsFid()
    {
        _wrapper.Setup(w => w.QueryVector(Gpkg, It.IsAny<VectorQuery>()))
            .Returns("{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"id\":42,\"properties\":{},\"geometry\":null}]}");

        var item = await CreateFeatures().GetItemAsync(Org, Ds, Layer.Name, "42");

        JObject.Parse(item)["id"]!.Value<long>().ShouldBe(42);
        _wrapper.Verify(w => w.QueryVector(Gpkg, It.Is<VectorQuery>(q =>
            Fids(q, 42) && q.MaxFeatures == 1 && q.OutputFormat == "application/json")), Times.Once);

        (await CreateFeatures().GetItemAsync(Org, Ds, Layer.Name, "not-a-fid")).ShouldBe("{}");
    }
}
