using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using NUnit.Framework;
using Shouldly;

namespace Registry.Web.Test.Ogc;

/// <summary>
/// A dataset with one entry whose name XML cannot carry (a control character) still has valid capabilities documents
/// in every service: the layer is left out by the layer catalog, the others are listed.
/// </summary>
[TestFixture]
public class OgcCapabilitiesNamesTests
{
    private static OgcContractWorld World() => new(["a\u0001.tif", "b.tif"]);

    [Test]
    public async Task WmsCapabilities_AreValidXml_AndListTheOtherLayer()
    {
        var xml = await World().Wms().GetCapabilitiesAsync("org", "ds", "1.3.0");

        var names = XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == "Layer")
            .Select(e => e.Elements().FirstOrDefault(c => c.Name.LocalName == "Name")?.Value)
            .Where(n => n != null).ToList();
        names.ShouldBe(["b.tif"]);
    }

    [Test]
    public async Task WmtsCapabilities_AreValidXml_AndListTheOtherLayer()
    {
        var xml = await World().Wmts().GetCapabilitiesAsync("org", "ds");

        var layers = XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == "Layer").ToList();
        layers.Select(l => l.Elements().First(c => c.Name.LocalName == "Identifier").Value).ShouldBe(["b.tif"]);
    }
}
