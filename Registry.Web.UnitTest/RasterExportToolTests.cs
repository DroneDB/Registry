using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-DEV-05: raster-export refuses (400, before the task is queued) a formula the raster cannot compute, with the
/// reason raster-metadata gives. libddb 2.0 computes it all the same and marks it disabled in its list of formulas;
/// libddb 2.1 refuses it itself (DDBERR_INVALIDARGS). Both come out as the same <see cref="ArgumentException"/>.
/// </summary>
[TestFixture]
public class RasterExportToolTests
{
    private const string Ortho = "ortho.tif";

    // The list of formulas of an RGB orthophoto, as libddb 2.0 gives it
    private static readonly string RgbMetadata = JsonSerializer.Serialize(new
    {
        statistics = new { },
        algorithms = new object[]
        {
            new { id = "VARI", name = "Visual Atmospheric Resistance Index" },
            new { id = "NDVI", name = "Normalized Difference Vegetation Index", disabled = true,
                disabledReason = "Requires bands: RN" }
        }
    });

    private RasterExportTool _tool = null!;
    private Mock<IRasterExportSizeCheck> _sizeCheck = null!;

    private Mock<IDDB> _ddb = null!;
    private Mock<IHeavyToolValidationContext> _ctx = null!;

    [SetUp]
    public void SetUp()
    {
        _sizeCheck = new Mock<IRasterExportSizeCheck>();
        _tool = ToolWith(abiMinor: 3);
        _ddb = new Mock<IDDB>(MockBehavior.Strict);
        _ddb.Setup(d => d.EntryExists(Ortho)).Returns(true);
        _ctx = new Mock<IHeavyToolValidationContext>();
        _ctx.SetupGet(c => c.Ddb).Returns(_ddb.Object);
    }

    // The tool on a libddb of this ABI minor (the recorded capabilities of libddb with another minor)
    private RasterExportTool ToolWith(int abiMinor) =>
        new(Microsoft.Extensions.Options.Options.Create(new ProcessingPlatformSettings()), _sizeCheck.Object,
            new Support.AbiMinorRuntime(abiMinor));

    private static HeavyToolRequest RequestWith(string paramsJson) =>
        new("raster-export", "1", "org", "ds", Ortho, JsonDocument.Parse(paramsJson).RootElement.Clone());

    private static HeavyToolRequest Request(string? formula, string? bandFilter = null) =>
        new("raster-export", "1", "org", "ds", Ortho, JsonSerializer.SerializeToElement(
            new Dictionary<string, object?> { ["formula"] = formula, ["bandFilter"] = bandFilter }
                .Where(kv => kv.Value != null).ToDictionary()));

    private Task Validate(HeavyToolRequest request) => _tool.ValidateAsync(request, _ctx.Object, CancellationToken.None);

    [Test]
    public async Task NdviOnAnRgbRaster_Libddb20_Is400WithTheReason()
    {
        _ddb.Setup(d => d.GetRasterMetadata(Ortho, "NDVI", null)).Returns(RgbMetadata);

        var ex = await Should.ThrowAsync<ArgumentException>(() => Validate(Request("NDVI")));

        ex.Message.ShouldBe("NDVI is not available for this raster: Requires bands: RN");
    }

    [Test]
    public async Task NdviOnAnRgbRaster_Libddb21_Is400WithTheReason()
    {
        _ddb.Setup(d => d.GetRasterMetadata(Ortho, "NDVI", "RGB"))
            .Throws(new DdbInvalidArgumentsException("Requires bands: RN"));

        var ex = await Should.ThrowAsync<ArgumentException>(() => Validate(Request("NDVI", "RGB")));

        ex.Message.ShouldBe("NDVI is not available for this raster: Requires bands: RN");
    }

    [Test]
    public async Task AnUnknownFormula_Is400UnknownFormula()
    {
        // libddb 2.1: refused
        _ddb.Setup(d => d.GetRasterMetadata(Ortho, "BOGUS", null))
            .Throws(new DdbInvalidArgumentsException("Unknown formula: BOGUS"));
        (await Should.ThrowAsync<ArgumentException>(() => Validate(Request("BOGUS"))))
            .Message.ShouldBe("Unknown formula: BOGUS");

        // libddb 2.0: a plain error (wrapped by the adapter); the list of formulas does not have it
        _ddb.Setup(d => d.GetRasterMetadata(Ortho, "BOGUS", null))
            .Throws(new InvalidOperationException("Cannot get raster metadata of 'ortho.tif'",
                new DdbException("Unknown formula: BOGUS")));
        _ddb.Setup(d => d.GetRasterMetadata(Ortho, null, null)).Returns(RgbMetadata);
        (await Should.ThrowAsync<ArgumentException>(() => Validate(Request("BOGUS"))))
            .Message.ShouldBe("Unknown formula: BOGUS");
    }

    // A single-band DEM: libddb's list marks the temperature formulas "Requires bands: T", the rendering computes them
    [TestCase("CELSIUS")]
    [TestCase("KELVIN")]
    [TestCase("FAHRENHEIT")] // D-022: the formula of libddb 2.3 reads the same band
    public async Task ATemperatureFormulaOnASingleBandDem_Passes(string formula)
    {
        var dem = JsonSerializer.Serialize(new
        {
            statistics = new { },
            algorithms = new object[]
            {
                new { id = "NDVI", disabled = true, disabledReason = "Requires bands: RN" },
                new { id = "CELSIUS", disabled = true, disabledReason = "Requires bands: T" },
                new { id = "KELVIN", disabled = true, disabledReason = "Requires bands: T" },
                new { id = "FAHRENHEIT", disabled = true, disabledReason = "Requires bands: T" }
            }
        });
        _ddb.Setup(d => d.GetRasterMetadata(Ortho, formula, null)).Returns(dem);

        await Validate(Request(formula));

        // A libddb that refuses the missing band itself does not stop it either
        _ddb.Setup(d => d.GetRasterMetadata(Ortho, formula, null))
            .Throws(new DdbInvalidArgumentsException("Requires bands: T"));
        await Validate(Request(formula));

        // NDVI on the same raster is still refused
        _ddb.Setup(d => d.GetRasterMetadata(Ortho, "NDVI", null)).Returns(dem);
        (await Should.ThrowAsync<ArgumentException>(() => Validate(Request("NDVI"))))
            .Message.ShouldBe("NDVI is not available for this raster: Requires bands: RN");
    }

    [Test]
    public async Task ATemperatureFormula_RefusedForAnotherReason_Is400()
    {
        _ddb.Setup(d => d.GetRasterMetadata(Ortho, "CELSIUS", null))
            .Throws(new DdbInvalidArgumentsException("The raster holds no temperature data"));

        (await Should.ThrowAsync<ArgumentException>(() => Validate(Request("CELSIUS"))))
            .Message.ShouldBe("CELSIUS is not available for this raster: The raster holds no temperature data");
    }

    [Test]
    public async Task AFormulaTheRasterComputes_Passes()
    {
        _ddb.Setup(d => d.GetRasterMetadata(Ortho, "VARI", null)).Returns(RgbMetadata);

        await Validate(Request("VARI"));
    }

    [Test]
    public async Task WithoutAFormula_TheMetadataIsNotRead()
    {
        await Validate(Request(null));

        _ddb.Verify(d => d.GetRasterMetadata(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Test]
    public async Task AnotherFailureOfTheMetadata_IsLeftToTheExport()
    {
        _ddb.Setup(d => d.GetRasterMetadata(Ortho, "VARI", null)).Throws(new InvalidOperationException("busy"));
        _ddb.Setup(d => d.GetRasterMetadata(Ortho, null, null)).Throws(new InvalidOperationException("busy"));

        await Validate(Request("VARI"));
    }

    // FR-RAS-16, D-023: the submit checks the export limit with the estimate of the value info (the check of the direct
    // export), for the entry of the request
    [Test]
    public async Task TheSubmit_ChecksTheExportLimitOfTheRaster()
    {
        await Validate(Request(null));

        _sizeCheck.Verify(c => c.EnsureWithinLimitAsync("org", "ds", Ortho, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task AnExportOverTheLimit_Is400AtTheSubmit()
    {
        _sizeCheck.Setup(c => c.EnsureWithinLimitAsync("org", "ds", Ortho, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BadRequestException("Export output exceeds maximum allowed size (1.1 GB > 1 GB)."));

        (await Should.ThrowAsync<BadRequestException>(() => Validate(Request(null))))
            .Message.ShouldBe("Export output exceeds maximum allowed size (1.1 GB > 1 GB).");
    }

    [Test]
    public async Task TheWorker_DoesNotCheckTheLimitAgain()
    {
        var worker = new Mock<IHeavyToolExecutionContext>();
        worker.SetupGet(c => c.Ddb).Returns(_ddb.Object);

        await _tool.ValidateAsync(Request(null), worker.Object, CancellationToken.None);

        _sizeCheck.Verify(c => c.EnsureWithinLimitAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<RasterAnalysisOptions?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    // FR-RAS-16, D-023: the estimate of the submit is the one of the window (the visible extent) with the NoData value
    [Test]
    public async Task TheSubmit_ChecksTheLimitWithTheWindowAndTheNodataValue()
    {
        await Validate(RequestWith("""{"bbox":[12.5,45.25,13,46],"nodata":0,"formula":null}"""));

        _sizeCheck.Verify(c => c.EnsureWithinLimitAsync("org", "ds", Ortho,
            It.Is<RasterAnalysisOptions?>(o => o != null && o.Nodata == 0 && o.Formula == null &&
                                                o.Bbox != null && o.Bbox.SequenceEqual(new[] { 12.5, 45.25, 13, 46.0 })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task AWindowOverTheLimit_Is400AtTheSubmitWithTheEstimateAndTheLimit()
    {
        _sizeCheck.Setup(c => c.EnsureWithinLimitAsync("org", "ds", Ortho, It.IsAny<RasterAnalysisOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ExportTooLargeException("Export output exceeds maximum allowed size (1.4 GB > 1 GB).",
                1_500_000_000, 1_073_741_824));

        var ex = await Should.ThrowAsync<ExportTooLargeException>(() =>
            Validate(RequestWith("""{"bbox":[12,45,13,46]}""")));

        ex.StatusCode.ShouldBe(400);
        (ex.EstimatedBytes, ex.LimitBytes).ShouldBe((1_500_000_000L, 1_073_741_824L));
    }

    // The window is exported by libddb 2.3: before it DDBExportRaster3 ignores it and would export the whole raster
    [Test]
    public async Task AWindow_OnLibddb22_IsRefusedAtTheBboxMemberBeforeTheSizeCheck()
    {
        var tool = ToolWith(abiMinor: 2);

        var ex = await Should.ThrowAsync<InvalidFieldException>(() =>
            tool.ValidateAsync(RequestWith("""{"bbox":[12,45,13,46]}"""), _ctx.Object, CancellationToken.None));

        ex.Field.ShouldBe("bbox");
        ex.StatusCode.ShouldBe(400);
        ex.Message.ShouldContain("libddb 2.3 or later");
        ex.Message.ShouldContain("ABI 2.2");
        _sizeCheck.VerifyNoOtherCalls();
    }

    [Test]
    public async Task TheWholeRaster_OnLibddb22_StillExports()
    {
        await ToolWith(abiMinor: 2).ValidateAsync(RequestWith("""{"nodata":0}"""), _ctx.Object, CancellationToken.None);

        _sizeCheck.Verify(c => c.EnsureWithinLimitAsync("org", "ds", Ortho,
            It.Is<RasterAnalysisOptions?>(o => o != null && o.Nodata == 0 && o.Bbox == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCase("[1,2,3]")]
    [TestCase("[1,2,3,4,5]")]
    [TestCase("[1,2,3,\"4\"]")]
    [TestCase("\"1,2,3,4\"")]
    [TestCase("{\"minLon\":1}")]
    [TestCase("[-181,0,10,10]")]
    [TestCase("[0,0,181,10]")]
    [TestCase("[0,-91,10,10]")]
    [TestCase("[0,0,10,91]")]
    public async Task ABboxThatIsNotAWgs84Window_Is400AtTheBboxMember(string bbox)
    {
        var ex = await Should.ThrowAsync<InvalidFieldException>(() =>
            Validate(RequestWith($$"""{"bbox":{{bbox}}}""")));

        ex.Field.ShouldBe("bbox");
        ex.FieldErrors!["bbox"].ShouldNotBeEmpty();
    }

    [TestCase("\"0\"")]
    [TestCase("true")]
    [TestCase("[0]")]
    public async Task ANodataThatIsNotANumber_Is400AtTheNodataMember(string nodata)
    {
        var ex = await Should.ThrowAsync<InvalidFieldException>(() =>
            Validate(RequestWith($$"""{"nodata":{{nodata}}}""")));

        ex.Field.ShouldBe("nodata");
    }

    [Test]
    public async Task TheExport_SendsTheWindowAndTheNodataValueToLibddb()
    {
        var workDir = Directory.CreateTempSubdirectory("raster-export-test-").FullName;
        try
        {
            RasterAnalysisOptions? sent = null;
            string? output = null;
            var ddb = new Mock<IDDB>(MockBehavior.Strict);
            ddb.Setup(d => d.ExportRaster(Ortho, It.IsAny<string>(), null, null, "CELSIUS", null, "ironbow", "10,40",
                    It.IsAny<int>(), It.IsAny<RasterAnalysisOptions?>(), It.IsAny<Action<double, string?>?>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, string, string?, string?, string?, string?, string?, string?, int, RasterAnalysisOptions?,
                    Action<double, string?>?, CancellationToken>((_, o, _, _, _, _, _, _, _, options, _, _) =>
                {
                    sent = options;
                    output = o;
                    File.WriteAllBytes(o, [1, 2, 3]);
                });
            var worker = new Mock<IHeavyToolExecutionContext>();
            worker.SetupGet(c => c.Ddb).Returns(ddb.Object);
            worker.SetupGet(c => c.WorkDir).Returns(workDir);

            var artifact = await _tool.ExecuteAsync(
                RequestWith("""{"formula":"CELSIUS","colormap":"ironbow","rescale":"10,40","nodata":-9999,"bbox":[12.5,45.25,13,46]}"""),
                worker.Object, new Progress<HeavyToolProgress>(), CancellationToken.None);

            sent.ShouldNotBeNull();
            sent.Nodata.ShouldBe(-9999);
            sent.Bbox.ShouldBe([12.5, 45.25, 13, 46]);
            sent.Formula.ShouldBeNull("the formula is the style of the export, not an option of the values");
            Path.GetFileName(output!).ShouldBe("ortho_export.tif");
            artifact.ShouldNotBeNull().SizeBytes.ShouldBe(3);
        }
        finally
        {
            Directory.Delete(workDir, true);
        }
    }

    [Test]
    public async Task TheExport_OfTheWholeRaster_SendsNoOptions()
    {
        var workDir = Directory.CreateTempSubdirectory("raster-export-test-").FullName;
        try
        {
            var calls = 0;
            RasterAnalysisOptions? sent = new(Nodata: 1);
            var ddb = new Mock<IDDB>(MockBehavior.Strict);
            ddb.Setup(d => d.ExportRaster(Ortho, It.IsAny<string>(), null, null, null, null, null, null,
                    It.IsAny<int>(), It.IsAny<RasterAnalysisOptions?>(), It.IsAny<Action<double, string?>?>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, string, string?, string?, string?, string?, string?, string?, int, RasterAnalysisOptions?,
                    Action<double, string?>?, CancellationToken>((_, o, _, _, _, _, _, _, _, options, _, _) =>
                {
                    calls++;
                    sent = options;
                    File.WriteAllBytes(o, [1]);
                });
            var worker = new Mock<IHeavyToolExecutionContext>();
            worker.SetupGet(c => c.Ddb).Returns(ddb.Object);
            worker.SetupGet(c => c.WorkDir).Returns(workDir);

            await _tool.ExecuteAsync(RequestWith("{}"), worker.Object, new Progress<HeavyToolProgress>(),
                CancellationToken.None);

            calls.ShouldBe(1);
            sent.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(workDir, true);
        }
    }

    [Test]
    public void TheToolSchema_ListsTheNodataTheWindowAndTheUnitOfTheRescale()
    {
        var properties = _tool.InputSchema.RootElement.GetProperty("properties");

        properties.GetProperty("bbox").GetProperty("minItems").GetInt32().ShouldBe(4);
        properties.GetProperty("bbox").GetProperty("maxItems").GetInt32().ShouldBe(4);
        properties.TryGetProperty("nodata", out _).ShouldBeTrue();
        properties.TryGetProperty("rescaleUnit", out _).ShouldBeTrue();
    }

    // D-022: a rescale in another unit than the formula's needs libddb 2.3; on 2.2 the submit refuses it at its own member,
    // before the formula is checked (the strict mock has no raster metadata)
    [Test]
    public async Task ARescaleUnit_OnLibddb22_Is400AtItsMember()
    {
        _tool = ToolWith(abiMinor: 2);

        var ex = await Should.ThrowAsync<InvalidFieldException>(() => Validate(
            RequestWith("""{"formula":"CELSIUS","rescale":"280,310","rescaleUnit":"K"}""")));

        ex.Field.ShouldBe("rescaleUnit");
        ex.Message.ShouldContain("needs libddb 2.3 or later");
    }

    // On 2.3 the unit reaches libddb in the options of the export (which converts the rescale), and only there: the size
    // estimate of the submit reads no unit
    [Test]
    public async Task TheExport_SendsTheUnitOfTheRescaleToLibddb()
    {
        var workDir = Directory.CreateTempSubdirectory("raster-export-test-").FullName;
        try
        {
            RasterAnalysisOptions? sent = null;
            var ddb = new Mock<IDDB>(MockBehavior.Strict);
            ddb.Setup(d => d.ExportRaster(Ortho, It.IsAny<string>(), null, null, "FAHRENHEIT", null, "ironbow", "280,310",
                    It.IsAny<int>(), It.IsAny<RasterAnalysisOptions?>(), It.IsAny<Action<double, string?>?>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, string, string?, string?, string?, string?, string?, string?, int, RasterAnalysisOptions?,
                    Action<double, string?>?, CancellationToken>((_, o, _, _, _, _, _, _, _, options, _, _) =>
                {
                    sent = options;
                    File.WriteAllBytes(o, [1]);
                });
            var worker = new Mock<IHeavyToolExecutionContext>();
            worker.SetupGet(c => c.Ddb).Returns(ddb.Object);
            worker.SetupGet(c => c.WorkDir).Returns(workDir);

            await _tool.ExecuteAsync(
                RequestWith("""{"formula":"FAHRENHEIT","colormap":"ironbow","rescale":"280,310","rescaleUnit":" K "}"""),
                worker.Object, new Progress<HeavyToolProgress>(), CancellationToken.None);

            sent.ShouldNotBeNull();
            sent.RescaleUnit.ShouldBe("K");
            sent.Nodata.ShouldBeNull();
            sent.Bbox.ShouldBeNull();
            sent.Formula.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(workDir, true);
        }
    }

    // FR-RAS-16, FR-XC-22: /sys/features says whether the loaded libddb exports a window, by the rule of the submit, so
    // the Hub disables 'Visible extent' with its reason where the submit would refuse the bbox
    [TestCase(2, false)]
    [TestCase(3, true)]
    [TestCase(4, true)]
    public async Task TheRasterExportWindowFeature_FollowsTheAbiMinor(int abiMinor, bool expected)
    {
        var features = new Registry.Web.Models.DTO.FeaturesDto();
        await new Registry.Web.Services.Features.RasterExportFeatures(new Support.AbiMinorRuntime(abiMinor)).FillAsync(features);

        features.RasterExportWindow.ShouldBe(expected);
    }
}
