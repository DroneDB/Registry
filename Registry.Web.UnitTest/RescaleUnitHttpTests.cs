using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Test.Baselines;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>A thermal image of the fixtures and the renders of it with a rescale in another unit (D-022).</summary>
public abstract class RescaleUnitFixture : DatasetAccessFixture
{
    protected const string Ds = "/orgs/" + PrivateOrg + "/ds/" + PrivateDs;
    protected const string Image = "thermal.jpg";

    // A Celsius layer whose rescale was written in Kelvin (the user changed the unit after setting it)
    protected const string ThermalStyle = "formula=CELSIUS&colormap=ironbow&rescale=280%2C310&rescaleUnit=K";

    protected static readonly byte[] Thumbnail = [1, 2, 3];

    [OneTimeSetUp]
    public async Task IndexTheImage()
    {
        var internalRef = await WithContextAsync(context =>
            Task.FromResult(context.Datasets.Single(d => d.Slug == PrivateDs).InternalRef));
        using var scope = Factory.Services.CreateScope();
        var folder = scope.ServiceProvider.GetRequiredService<IDdbManager>().Get(PrivateOrg, internalRef).DatasetFolderPath;
        Factory.DdbWrapper.Setup(d => d.GetEntry(folder, Image))
            .Returns(new Entry { Path = Image, Hash = "rescale-unit-hash", Type = EntryType.GeoImage, Size = 1 });
    }

    [SetUp]
    public void RenderAnything()
    {
        Factory.DdbWrapper.Invocations.Clear();
        Factory.DdbWrapper.Setup(d => d.GenerateThumbnailEx(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<RasterAnalysisOptions?>()))
            .Returns(Thumbnail);
    }

    protected Task<(HttpResponseMessage Response, string Body, System.Text.Json.Nodes.JsonObject? Json)> ThumbEx(
        string query) =>
        SendAsync(Request(HttpMethod.Get, $"{Ds}/thumb-ex?path={Image}&size=64&{query}", Caller.Owner));

    protected Task<(HttpResponseMessage Response, string Body, System.Text.Json.Nodes.JsonObject? Json)> SubmitExport(
        string paramsJson) =>
        SendAsync(Request(HttpMethod.Post, Ds + "/tasks", Caller.Owner, Json(
            $$"""{"toolId":"raster-export","path":"{{Image}}","params":{{paramsJson}},"force":true}""")));

    protected void ShouldNotHaveRendered() =>
        Factory.DdbWrapper.Invocations.ShouldNotContain(i => i.Method.Name == nameof(IDdbWrapper.GenerateThumbnailEx));
}

/// <summary>
/// D-022 on a libddb 2.2 (no <c>DDBRasterOptions.rescaleUnit</c>; the recorded capabilities with ABI 2.2, whatever the
/// vendored fixtures say): a render or an export that names the unit of its rescale is a 400 at <c>errors.rescaleUnit</c>
/// that says why, before any native work, and a render without it is unchanged.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~RescaleUnitHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class RescaleUnitHttpTests : RescaleUnitFixture
{
    protected override Action<IServiceCollection> FactoryServices => services =>
    {
        services.RemoveAll<IDdbRuntime>();
        services.AddSingleton<IDdbRuntime>(new AbiMinorRuntime(2));
    };

    [Test]
    public async Task AThumbnailWithARescaleUnit_OnLibddb22_Is400AtRescaleUnit()
    {
        var (response, body, json) = await ThumbEx(ThermalStyle);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        json!["errors"]!["rescaleUnit"]![0]!.GetValue<string>().ShouldContain("needs libddb 2.3 or later");
        ShouldNotHaveRendered();
    }

    [Test]
    public async Task ATileWithARescaleUnit_OnLibddb22_Is400AtRescaleUnit()
    {
        var (response, body, json) = await SendAsync(Request(HttpMethod.Get,
            $"{Ds}/tiles-ex/18/1/2.png?path={Image}&{ThermalStyle}", Caller.Owner));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        json!["errors"]!["rescaleUnit"]![0]!.GetValue<string>().ShouldContain("needs libddb 2.3 or later");
        Factory.DdbWrapper.Invocations.ShouldNotContain(i => i.Method.Name == nameof(IDdbWrapper.GenerateMemoryTileEx));
    }

    [Test]
    public async Task AnExportWithARescaleUnit_OnLibddb22_Is400AtRescaleUnit()
    {
        var (response, body, json) = await SendAsync(Request(HttpMethod.Get,
            $"{Ds}/export?path={Image}&{ThermalStyle}", Caller.Owner));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        json!["errors"]!["rescaleUnit"]![0]!.GetValue<string>().ShouldContain("needs libddb 2.3 or later");
    }

    // The task schema has the member (not an unknown property), and the submit refuses it on 2.2 at its own member
    [Test]
    public async Task AnExportTaskWithARescaleUnit_OnLibddb22_Is400AtRescaleUnit()
    {
        var (response, body, json) = await SubmitExport(
            """{"formula":"CELSIUS","colormap":"ironbow","rescale":"280,310","rescaleUnit":"K"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        json!["errors"]!["rescaleUnit"]![0]!.GetValue<string>().ShouldContain("needs libddb 2.3 or later");
    }

    // Without the unit nothing changes on 2.2: the render without options
    [Test]
    public async Task AThumbnailWithoutARescaleUnit_IsRenderedWithoutOptions()
    {
        Factory.DdbWrapper.Setup(d => d.GenerateThumbnailEx(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(Thumbnail);

        var (response, body, _) = await ThumbEx("formula=CELSIUS&colormap=ironbow&rescale=7%2C37");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        Factory.DdbWrapper.Verify(d => d.GenerateThumbnailEx(It.IsAny<string>(), It.IsAny<int>(), null, null, "CELSIUS",
            null, "ironbow", "7,37"), Times.Once);
    }
}

/// <summary>
/// D-022 on a libddb that reads the unit of the rescale (ABI 2.3, a runtime that reports it): the unit reaches libddb in the
/// options of the render, which converts the rescale to the unit of the formula, and it is part of the cache key.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~RescaleUnitAbi23HttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class RescaleUnitAbi23HttpTests : RescaleUnitFixture
{
    protected override Action<IServiceCollection> FactoryServices => services =>
    {
        services.RemoveAll<IDdbRuntime>();
        services.AddSingleton<IDdbRuntime>(new AbiMinorRuntime(3));
    };

    [Test]
    public async Task AThumbnailWithARescaleUnit_SendsTheUnitInTheOptions()
    {
        RasterAnalysisOptions? seen = null;
        Factory.DdbWrapper.Setup(d => d.GenerateThumbnailEx(It.IsAny<string>(), 64, null, null, "CELSIUS", null,
                "ironbow", "280,310", It.IsAny<RasterAnalysisOptions?>()))
            .Callback((string _, int _, string? _, string? _, string? _, string? _, string? _, string? _,
                RasterAnalysisOptions? o) => seen = o)
            .Returns(Thumbnail);

        var (response, body, _) = await ThumbEx(ThermalStyle);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        seen.ShouldNotBeNull();
        seen.RescaleUnit.ShouldBe("K");
        seen.Nodata.ShouldBeNull();
        seen.Formula.ShouldBeNull();
    }

    // The same style in another unit is another render: the cache does not answer the first one
    [Test]
    public async Task TheUnitOfTheRescale_IsPartOfTheCacheKey()
    {
        (await ThumbEx("formula=CELSIUS&colormap=ironbow&rescale=1%2C2&rescaleUnit=K")).Response.StatusCode
            .ShouldBe(HttpStatusCode.OK);
        (await ThumbEx("formula=CELSIUS&colormap=ironbow&rescale=1%2C2&rescaleUnit=F")).Response.StatusCode
            .ShouldBe(HttpStatusCode.OK);

        Factory.DdbWrapper.Invocations.Count(i => i.Method.Name == nameof(IDdbWrapper.GenerateThumbnailEx))
            .ShouldBe(2);
    }
}
