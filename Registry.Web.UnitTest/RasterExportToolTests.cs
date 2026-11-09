using System;
using System.Collections.Generic;
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
        _tool = new RasterExportTool(Microsoft.Extensions.Options.Options.Create(new ProcessingPlatformSettings()),
            _sizeCheck.Object);
        _ddb = new Mock<IDDB>(MockBehavior.Strict);
        _ddb.Setup(d => d.EntryExists(Ortho)).Returns(true);
        _ctx = new Mock<IHeavyToolValidationContext>();
        _ctx.SetupGet(c => c.Ddb).Returns(_ddb.Object);
    }

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
    public async Task ATemperatureFormulaOnASingleBandDem_Passes(string formula)
    {
        var dem = JsonSerializer.Serialize(new
        {
            statistics = new { },
            algorithms = new object[]
            {
                new { id = "NDVI", disabled = true, disabledReason = "Requires bands: RN" },
                new { id = "CELSIUS", disabled = true, disabledReason = "Requires bands: T" },
                new { id = "KELVIN", disabled = true, disabledReason = "Requires bands: T" }
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
}
