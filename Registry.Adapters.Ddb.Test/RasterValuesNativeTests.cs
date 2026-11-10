using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Runtime.InteropServices;
using System.Text.Json;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Shouldly;
using static Registry.Adapters.Ddb.Test.LibddbNative;

namespace Registry.Adapters.Ddb.Test;

/// <summary>
/// The raster values on the real libddb (U-201, FR-RAS-02, FR-RAS-04, FR-RAS-31): a point and an area by WGS 84
/// coordinates, found with libddb's one geo-to-pixel conversion on the file itself, in the units of the band (a UInt16 band
/// with a scale of 0.1 and the unit Celsius), the NoData override, and the options struct. The expected values are those of
/// <c>gdallocationinfo -wgs84</c> and <c>gdalinfo -stats</c> on the same files (the pixel (x, y) of the 20 x 20 rasters
/// holds 115 + x + 2y, read as that x 0.1).
/// Run: dotnet test Registry.Adapters.Ddb.Test --filter "FullyQualifiedName~RasterValuesNativeTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Native)]
public class RasterValuesNativeTests : TestBase
{
    // 20 x 20, 2 m pixels, EPSG:32633 from (500000, 5000000); and EPSG:3857 from (1670000, 5645600)
    internal const string UtmTiff = "SUkqAOQBAAAQAAABAwABAAAAFAAAAAEBAwABAAAAFAAAAAIBAwABAAAAEAAAAAMBAwABAAAACAAAAAYBAwABAAAAAQAAABEBBAABAAAAAAAAABUBAwABAAAAAQAAABYBAwABAAAAFAAAABcBBAABAAAAAAAAABwBAwABAAAAAQAAAD0BAwABAAAAAQAAAFMBAwABAAAAAQAAAA6DDAADAAAAzgAAAIKEDAAGAAAA5gAAAK+HAwAgAAAAFgEAALGHAgAeAAAAVgEAAAAAAAAAAAAAAAAAQAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgIQeQQAAAADQElNBAAAAAAAAAAABAAEAAAAHAAAEAAABAAEAAQQAAAEAAQACBLGHFgAAAAEIsYcHABYABggAAAEAjiMADAAAAQB5fwQMAAABACkjV0dTIDg0IC8gVVRNIHpvbmUgMzNOfFdHUyA4NHwAeJyNysMBxAAAAMHt+WzbthkUmRZ23pMiTYYsOfIUKFKiTIUqNeo0aNKiTYeuOj366gwYqjNirM6EqToz5uosWKqzYq3Ohq06O/bqHDiqc+KszoWrOjfu6jx4qvPirc6Hrzo//uoEhOpExCSqQuA5ABEAAAEDAAEAAAAUAAAAAQEDAAEAAAAUAAAAAgEDAAEAAAAQAAAAAwEDAAEAAAAIAAAABgEDAAEAAAABAAAAEQEEAAEAAAB0AQAAFQEDAAEAAAABAAAAFgEDAAEAAAAUAAAAFwEEAAEAAABvAAAAHAEDAAEAAAABAAAAPQEDAAEAAAABAAAAUwEDAAEAAAABAAAADoMMAAMAAAC2AgAAgoQMAAYAAADOAgAAr4cDACAAAAD+AgAAsYcCAB4AAAA+AwAAgKQCAOIAAABcAwAAAAAAAAAAAAAAAABAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAhB5BAAAAANASU0EAAAAAAAAAAAEAAQAAAAcAAAQAAAEAAQABBAAAAQABAAIEsYcWAAAAAQixhwcAFgAGCAAAAQCOIwAMAAABAHl/BAwAAAEAKSNXR1MgODQgLyBVVE0gem9uZSAzM058V0dTIDg0fAA8R0RBTE1ldGFkYXRhPgogIDxJdGVtIG5hbWU9Ik9GRlNFVCIgc2FtcGxlPSIwIiByb2xlPSJvZmZzZXQiPjA8L0l0ZW0+CiAgPEl0ZW0gbmFtZT0iU0NBTEUiIHNhbXBsZT0iMCIgcm9sZT0ic2NhbGUiPjAuMTAwMDAwMDAwMDAwMDAwMDE8L0l0ZW0+CiAgPEl0ZW0gbmFtZT0iVU5JVFRZUEUiIHNhbXBsZT0iMCIgcm9sZT0idW5pdHR5cGUiPkNlbHNpdXM8L0l0ZW0+CjwvR0RBTE1ldGFkYXRhPgoA";
    private const string WebMercatorTiff = "SUkqAOYBAAAQAAABAwABAAAAFAAAAAEBAwABAAAAFAAAAAIBAwABAAAAEAAAAAMBAwABAAAACAAAAAYBAwABAAAAAQAAABEBBAABAAAAAAAAABUBAwABAAAAAQAAABYBAwABAAAAFAAAABcBBAABAAAAAAAAABwBAwABAAAAAQAAAD0BAwABAAAAAQAAAFMBAwABAAAAAQAAAA6DDAADAAAAzgAAAIKEDAAGAAAA5gAAAK+HAwAgAAAAFgEAALGHAgAhAAAAVgEAAAAAAAAAAAAAAAAAQAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAcHs5QQAAAABIiVVBAAAAAAAAAAABAAEAAAAHAAAEAAABAAEAAQQAAAEAAQACBLGHGQAAAAEIsYcHABkABggAAAEAjiMADAAAAQARDwQMAAABACkjV0dTIDg0IC8gUHNldWRvLU1lcmNhdG9yfFdHUyA4NHwAeJyNysMBxAAAAMHt+WzbthkUmRZ23pMiTYYsOfIUKFKiTIUqNeo0aNKiTYeuOj366gwYqjNirM6EqToz5uosWKqzYq3Ohq06O/bqHDiqc+KszoWrOjfu6jx4qvPirc6Hrzo//uoEhOpExCSqQuA5EQAAAQMAAQAAABQAAAABAQMAAQAAABQAAAACAQMAAQAAABAAAAADAQMAAQAAAAgAAAAGAQMAAQAAAAEAAAARAQQAAQAAAHcBAAAVAQMAAQAAAAEAAAAWAQMAAQAAABQAAAAXAQQAAQAAAG8AAAAcAQMAAQAAAAEAAAA9AQMAAQAAAAEAAABTAQMAAQAAAAEAAAAOgwwAAwAAALgCAACChAwABgAAANACAACvhwMAIAAAAAADAACxhwIAIQAAAEADAACApAIA4gAAAGIDAAAAAAAAAAAAAAAAAEAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAHB7OUEAAAAASIlVQQAAAAAAAAAAAQABAAAABwAABAAAAQABAAEEAAABAAEAAgSxhxkAAAABCLGHBwAZAAYIAAABAI4jAAwAAAEAEQ8EDAAAAQApI1dHUyA4NCAvIFBzZXVkby1NZXJjYXRvcnxXR1MgODR8AAA8R0RBTE1ldGFkYXRhPgogIDxJdGVtIG5hbWU9Ik9GRlNFVCIgc2FtcGxlPSIwIiByb2xlPSJvZmZzZXQiPjA8L0l0ZW0+CiAgPEl0ZW0gbmFtZT0iU0NBTEUiIHNhbXBsZT0iMCIgcm9sZT0ic2NhbGUiPjAuMTAwMDAwMDAwMDAwMDAwMDE8L0l0ZW0+CiAgPEl0ZW0gbmFtZT0iVU5JVFRZUEUiIHNhbXBsZT0iMCIgcm9sZT0idW5pdHR5cGUiPkNlbHNpdXM8L0l0ZW0+CjwvR0RBTE1ldGFkYXRhPgoA";

    private static readonly NativeDdbWrapper Wrapper = new();
    private string _folder = null!;
    private string _utm = null!;
    private string _web = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        UseDdbFolder();
        var (gate, message) = CheckAbi();
        if (gate != Gate.Applies)
            Assert.Fail(message);

        new NativeDdbRuntime().RegisterProcess();
        _folder = Path.Combine(Path.GetTempPath(), nameof(RasterValuesNativeTests) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _utm = Path.Combine(_folder, "utm.tif");
        _web = Path.Combine(_folder, "web.tif");
        File.WriteAllBytes(_utm, Convert.FromBase64String(UtmTiff));
        File.WriteAllBytes(_web, Convert.FromBase64String(WebMercatorTiff));
    }

    [OneTimeTearDown]
    public void DeleteFolder() => Directory.Delete(_folder, true);

    [Test]
    public void TheOptionsStruct_IsTheLayoutOfDDBRasterOptions()
    {
        unsafe
        {
            // The struct of ABI 2.4: rescaleUnit is appended after formula
            sizeof(NativeRasterOptions).ShouldBe(64);
            Marshal.OffsetOf<NativeRasterOptions>(nameof(NativeRasterOptions.Flags)).ToInt32().ShouldBe(4);
            Marshal.OffsetOf<NativeRasterOptions>(nameof(NativeRasterOptions.Nodata)).ToInt32().ShouldBe(8);
            Marshal.OffsetOf<NativeRasterOptions>(nameof(NativeRasterOptions.Formula)).ToInt32().ShouldBe(48);
            Marshal.OffsetOf<NativeRasterOptions>(nameof(NativeRasterOptions.RescaleUnit)).ToInt32().ShouldBe(56);
        }
    }

    [Test]
    public void TheOptionsHandle_SetsTheUnitOfTheRescale_AndOnlyWhatIsGiven()
    {
        unsafe
        {
            using (var none = NativeRasterOptionsHandle.Create(null, null, null, "  "))
                ((IntPtr)none.Pointer).ShouldBe(IntPtr.Zero);

            using var handle = NativeRasterOptionsHandle.Create(null, null, null, " K ");
            var native = handle.Pointer;
            native->StructSize.ShouldBe(64u);
            native->Flags.ShouldBe(0u);
            native->Formula.ShouldBe(IntPtr.Zero);
            Marshal.PtrToStringUTF8(native->RescaleUnit).ShouldBe("K");
        }
    }

    // D-022 (U-202): the thumbnail of a Celsius band with the formula CELSIUS and the rescale 283.15..293.15 written in K is
    // the thumbnail with the rescale 10..20 in °C (both ends are exact in binary: 283.15 - 273.15 = 10)
    [Test]
    public void ARescaleInKelvin_IsConvertedToTheUnitOfTheFormula()
    {
        var inCelsius = Wrapper.GenerateThumbnailEx(_utm, 64, null, null, "CELSIUS", null, "ironbow", "10,20",
            RasterAnalysisOptions.None);
        var unconverted = Wrapper.GenerateThumbnailEx(_utm, 64, null, null, "CELSIUS", null, "ironbow", "283.15,293.15",
            RasterAnalysisOptions.None);

        var inKelvin = Wrapper.GenerateThumbnailEx(_utm, 64, null, null, "CELSIUS", null, "ironbow", "283.15,293.15",
            new RasterAnalysisOptions(RescaleUnit: "K"));

        inKelvin.ShouldBe(inCelsius);
        unconverted.ShouldNotBe(inCelsius);
    }

    // The 64-byte struct with rescaleUnit unset (its bytes past 56 are zero): a NoData render
    [Test]
    public void ARenderWithTheOptionsStruct_WithoutAUnit_Works()
    {
        var thumbnail = Wrapper.GenerateThumbnailEx(_utm, 64, null, null, "CELSIUS", null, "ironbow", "10,20",
            new RasterAnalysisOptions(Nodata: 130));

        thumbnail.Length.ShouldBeGreaterThan(0);
    }

    // gdallocationinfo -wgs84 at (15.0001399406774, 45.1533781647436) on utm.tif: 130 (pixel 5, 5)
    [Test]
    public void APointOnAUtmRaster_IsThePixelUnderIt_InTheUnitsOfTheBand()
    {
        var value = Parse(Wrapper.GetRasterPointValue(_utm, 15.0001399406774, 45.1533781647436));

        value.GetProperty("x").GetInt32().ShouldBe(5);
        value.GetProperty("y").GetInt32().ShouldBe(5);
        value.GetProperty("rawValue").GetDouble().ShouldBe(130);
        value.GetProperty("value").GetDouble().ShouldBe(13.0);
    }

    [Test]
    public void APointOnAWebMercatorRaster_IsThePixelUnderIt()
    {
        var value = Parse(Wrapper.GetRasterPointValue(_web, 15.0019640594773, 45.1526740539866));

        value.GetProperty("x").GetInt32().ShouldBe(5);
        value.GetProperty("y").GetInt32().ShouldBe(5);
        value.GetProperty("value").GetDouble().ShouldBe(13.0);
    }

    [Test]
    public void APointOutsideTheRaster_IsInvalidArguments() =>
        Should.Throw<DdbInvalidArgumentsException>(() => Wrapper.GetRasterPointValue(_utm, 16.0, 45.15));

    // The NoData override of 130 leaves the pixel without a value
    [Test]
    public void ThePointOnTheNodataOverride_HasNoValue() =>
        Should.Throw<DdbInvalidArgumentsException>(() => Wrapper.GetRasterPointValue(_utm, 15.0001399406774,
            45.1533781647436, new RasterAnalysisOptions(Nodata: 130)));

    // Pixels 5..9 x 10..14 of web.tif, between the corners (1670010, 5645570) and (1670020, 5645580) of gdaltransform:
    // raw 140..152 (gdal: min 140, max 152, mean 146 over 25 pixels), read as 14.0..15.2, mean 14.6
    [Test]
    public void AnArea_IsThePixelsUnderTheRectangle()
    {
        var stats = Parse(Wrapper.GetRasterAreaStats(_web, 15.0019550763244, 45.1525536869656, 15.0020449078528,
            45.152617038061));

        stats.GetProperty("pixelCount").GetInt32().ShouldBe(25);
        stats.GetProperty("min").GetDouble().ShouldBe(14.0, 1e-5);
        stats.GetProperty("max").GetDouble().ShouldBe(15.2, 1e-5);
        stats.GetProperty("mean").GetDouble().ShouldBe(14.6, 1e-5);
        stats.GetProperty("unit").GetString().ShouldBe("Celsius");
    }

    // FR-RAS-16: the export of a window. The box of the area test above holds pixels 5..9 x 10..14 of web.tif (25 pixels,
    // by gdaltransform of the corners), so the export is 5 x 5 pixels of the same 2 m
    private static readonly double[] Window = [15.0019550763244, 45.1525536869656, 15.0020449078528, 45.152617038061];

    private JsonElement ExportWindow(string name)
    {
        var output = Path.Combine(_folder, name);
        Wrapper.ExportRaster(_web, output, null, null, null, null, null, "0,255", 0, new RasterAnalysisOptions(Bbox: Window),
            null, default);
        return Parse(Wrapper.GetRasterValueInfo(output, RasterAnalysisOptions.None));
    }

    [Test]
    public void TheExportOfAWindow_IsThePixelsUnderTheBox()
    {
        var exported = ExportWindow("window.tif");

        exported.GetProperty("width").GetInt32().ShouldBe(5);
        exported.GetProperty("height").GetInt32().ShouldBe(5);
        // The same pixels: raw 140..152 (gdalinfo -stats of the area above), the Byte copy of 0..255 keeps them and the
        // scale of 0.1 of the band: 14.0..15.2
        exported.GetProperty("valueMin").GetDouble().ShouldBe(14.0, 1e-5);
        exported.GetProperty("valueMax").GetDouble().ShouldBe(15.2, 1e-5);
    }

    // A window with no pixel of the raster is invalid arguments
    [Test]
    public void TheExportOfAWindowOutsideTheRaster_IsInvalidArguments()
    {
        var output = Path.Combine(_folder, "outside.tif");
        Should.Throw<DdbInvalidArgumentsException>(() => Wrapper.ExportRaster(_web, output, null, null, null, null, null,
            "0,255", 0, new RasterAnalysisOptions(Bbox: [16, 46, 16.1, 46.1]), null, default));
        File.Exists(output).ShouldBeFalse();
    }

    [Test]
    public void TheValueInfo_IsExactAndPhysical_WithTheExportSizeInputs()
    {
        var info = Parse(Wrapper.GetRasterValueInfo(_utm, RasterAnalysisOptions.None));

        info.GetProperty("valueMin").GetDouble().ShouldBe(11.5, 1e-9);
        info.GetProperty("valueMax").GetDouble().ShouldBe(17.2, 1e-5); // 172 x 0.1
        info.GetProperty("pixelCount").GetInt64().ShouldBe(400);
        info.GetProperty("validPixelCount").GetInt64().ShouldBe(400);
        info.GetProperty("nodataInvalid").GetBoolean().ShouldBeFalse();
        info.GetProperty("stretch").GetArrayLength().ShouldBe(1);
    }

    [Test]
    public void TheValueInfoOfAWindow_CountsTheSourcePixelsInIt()
    {
        // The whole raster and a bit more
        var info = Parse(Wrapper.GetRasterValueInfo(_web,
            new RasterAnalysisOptions(Bbox: [15.001, 45.151, 15.004, 45.154])));

        info.GetProperty("windowPixelCount").GetInt64().ShouldBe(400);
    }

    // FR-RAS-16: the value info of a box with no pixel of the raster (a degree east of it) does not fail; it counts no pixel
    // in it, which the submit of a window export refuses (the export of libddb would be invalid arguments)
    [Test]
    public void TheValueInfoOfAWindowOutsideTheRaster_CountsNoPixel()
    {
        var info = Parse(Wrapper.GetRasterValueInfo(_web, new RasterAnalysisOptions(Bbox: [16, 46, 16.1, 46.1])));

        info.GetProperty("windowPixelCount").GetInt64().ShouldBe(0);
        info.GetProperty("validPixelCount").GetInt64().ShouldBe(400);
    }

    [Test]
    public void TheValueInfoWithANodataOverride_ExcludesIt()
    {
        var info = Parse(Wrapper.GetRasterValueInfo(_utm, new RasterAnalysisOptions(Nodata: 115)));

        info.GetProperty("validPixelCount").GetInt64().ShouldBe(399);
        info.GetProperty("nodata").GetDouble().ShouldBe(115);
        info.GetProperty("nodataOverridden").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public void ANodataValueThatIsNotFinite_IsRefusedBeforeTheCall() =>
        Should.Throw<ArgumentException>(() =>
            Wrapper.GetRasterValueInfo(_utm, new RasterAnalysisOptions(Nodata: double.NaN)));

    // The pixel (5, 5) holds 115 + 5 + 2 x 5 = 130 (13.0 with the scale of 0.1); no options read it as they are
    [Test]
    public void APixelWithoutOptions_IsReadByThePixelExport()
    {
        var value = Parse(Wrapper.GetRasterPointValue(_utm, 5, 5, RasterAnalysisOptions.None));

        value.GetProperty("rawValue").GetDouble().ShouldBe(130);
        value.GetProperty("value").GetDouble().ShouldBe(13.0);
        Parse(Wrapper.GetRasterAreaStats(_utm, 5, 5, 6, 6, null)).GetProperty("pixelCount").GetInt32().ShouldBe(4);
    }

    // libddb applies the options at a pixel (DDBGetRasterPointValuePixelEx): the NoData override of 130 leaves the
    // pixel (5, 5) without a value, and KELVIN reads 13.0 Celsius as 286.15 K
    [Test]
    public void APixelWithOptions_IsReadWithThem()
    {
        Should.Throw<DdbInvalidArgumentsException>(() =>
            Wrapper.GetRasterPointValue(_utm, 5, 5, new RasterAnalysisOptions(Nodata: 130)));

        var kelvin = Parse(Wrapper.GetRasterPointValue(_utm, 5, 5, new RasterAnalysisOptions(Formula: "KELVIN")));
        kelvin.GetProperty("value").GetDouble().ShouldBe(286.15, 1e-6);
        kelvin.GetProperty("unit").GetString().ShouldBe("K");
    }

    // Pixels (5, 5) .. (6, 6) hold 130, 131, 132, 133 (DDBGetRasterAreaStatsPixelEx): without 130 three are left, 13.1 to
    // 13.3, mean 13.2
    [Test]
    public void APixelRectangleWithANodataOverride_LeavesItsPixelsOut()
    {
        var stats = Parse(Wrapper.GetRasterAreaStats(_utm, 5, 5, 6, 6, new RasterAnalysisOptions(Nodata: 130)));

        stats.GetProperty("pixelCount").GetInt32().ShouldBe(3);
        stats.GetProperty("min").GetDouble().ShouldBe(13.1, 1e-5);
        stats.GetProperty("max").GetDouble().ShouldBe(13.3, 1e-5);
        stats.GetProperty("mean").GetDouble().ShouldBe(13.2, 1e-5);
    }

    // A formula that is not a thermal one is refused by libddb, never dropped
    [Test]
    public void APixelWithANonThermalFormula_IsRefused() =>
        Should.Throw<DdbInvalidArgumentsException>(() =>
            Wrapper.GetRasterAreaStats(_utm, 5, 5, 6, 6, new RasterAnalysisOptions(Formula: "NDVI")));

    // FR-RAS-11, NFR-001: the contours report their progress (statistics, levels, lines) and say whether the lines were cut
    // at the cap (truncated, featureCap). The values 11.5 .. 17.2 of utm.tif have the levels 12 .. 17 at interval 1.
    [Test]
    public void TheContours_ReportTheirProgress_AndSayTheyAreNotCut()
    {
        var seen = new List<(double Fraction, string? Phase)>();

        var contours = Parse(Wrapper.GenerateContours(_utm, 1.0, null, 0, null, null, 0, 1, null,
            (fraction, phase) => seen.Add((fraction, phase)), CancellationToken.None));

        contours.GetProperty("min").GetDouble().ShouldBe(12);
        contours.GetProperty("max").GetDouble().ShouldBe(17);
        seen.First().ShouldBe((0.0, "statistics"));
        seen.Last().ShouldBe((1.0, "lines"));
        seen.Select(x => x.Phase).Distinct().ShouldBe(["statistics", "levels", "lines"]);
        seen.Select(x => x.Fraction).ShouldBe(seen.Select(x => x.Fraction).Order());
        contours.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        contours.GetProperty("featureCap").GetInt32().ShouldBe(100000);
    }

    [Test]
    public void TheCancelOfTheToken_StopsTheContours()
    {
        using var cts = new CancellationTokenSource();

        var ex = Should.Throw<DdbCanceledException>(() => Wrapper.GenerateContours(_utm, 1.0, null, 0, null, null, 0, 1,
            null, (_, phase) =>
            {
                if (phase == "levels")
                    cts.Cancel();
            }, cts.Token));

        ex.CancellationToken.ShouldBe(cts.Token);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
}
