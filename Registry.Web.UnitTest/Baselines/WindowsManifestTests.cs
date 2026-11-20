using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// libddb runs inside Registry.Web.exe on Windows, so its builds can use paths longer than 260
/// characters only when the executable's manifest declares longPathAware. The project must embed
/// Registry.Web/app.manifest, and the manifest must declare it in the namespace Windows reads and keep
/// the asInvoker level of the manifest the SDK embeds by default. The release workflow checks the
/// published executables.
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class WindowsManifestTests
{
    private static readonly XNamespace Asm1 = "urn:schemas-microsoft-com:asm.v1";
    private static readonly XNamespace Asm3 = "urn:schemas-microsoft-com:asm.v3";
    private static readonly XNamespace WindowsSettings2016 = "http://schemas.microsoft.com/SMI/2016/WindowsSettings";

    [Test]
    public void TheProjectEmbedsTheManifest()
    {
        var project = XDocument.Load(RepositoryFiles.PathOf("Registry.Web/Registry.Web.csproj"));
        project.Descendants("ApplicationManifest").Select(e => e.Value.Trim()).ShouldBe(["app.manifest"]);
    }

    [Test]
    public void TheManifestDeclaresLongPathAware()
    {
        var manifest = XDocument.Load(RepositoryFiles.PathOf("Registry.Web/app.manifest"));

        manifest.Root!.Name.ShouldBe(Asm1 + "assembly");
        manifest.Root.Elements(Asm3 + "application").Elements(Asm3 + "windowsSettings")
            .Elements(WindowsSettings2016 + "longPathAware").Select(e => e.Value.Trim())
            .ShouldBe(["true"]);
        manifest.Descendants(Asm3 + "requestedExecutionLevel").Select(e => (string?)e.Attribute("level"))
            .ShouldBe(["asInvoker"]);
    }
}
