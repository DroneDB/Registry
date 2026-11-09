using System;
using System.IO;
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
    private const string UtmTiff = "SUkqAOQBAAAQAAABAwABAAAAFAAAAAEBAwABAAAAFAAAAAIBAwABAAAAEAAAAAMBAwABAAAACAAAAAYBAwABAAAAAQAAABEBBAABAAAAAAAAABUBAwABAAAAAQAAABYBAwABAAAAFAAAABcBBAABAAAAAAAAABwBAwABAAAAAQAAAD0BAwABAAAAAQAAAFMBAwABAAAAAQAAAA6DDAADAAAAzgAAAIKEDAAGAAAA5gAAAK+HAwAgAAAAFgEAALGHAgAeAAAAVgEAAAAAAAAAAAAAAAAAQAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgIQeQQAAAADQElNBAAAAAAAAAAABAAEAAAAHAAAEAAABAAEAAQQAAAEAAQACBLGHFgAAAAEIsYcHABYABggAAAEAjiMADAAAAQB5fwQMAAABACkjV0dTIDg0IC8gVVRNIHpvbmUgMzNOfFdHUyA4NHwAeJyNysMBxAAAAMHt+WzbthkUmRZ23pMiTYYsOfIUKFKiTIUqNeo0aNKiTYeuOj366gwYqjNirM6EqToz5uosWKqzYq3Ohq06O/bqHDiqc+KszoWrOjfu6jx4qvPirc6Hrzo//uoEhOpExCSqQuA5ABEAAAEDAAEAAAAUAAAAAQEDAAEAAAAUAAAAAgEDAAEAAAAQAAAAAwEDAAEAAAAIAAAABgEDAAEAAAABAAAAEQEEAAEAAAB0AQAAFQEDAAEAAAABAAAAFgEDAAEAAAAUAAAAFwEEAAEAAABvAAAAHAEDAAEAAAABAAAAPQEDAAEAAAABAAAAUwEDAAEAAAABAAAADoMMAAMAAAC2AgAAgoQMAAYAAADOAgAAr4cDACAAAAD+AgAAsYcCAB4AAAA+AwAAgKQCAOIAAABcAwAAAAAAAAAAAAAAAABAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAhB5BAAAAANASU0EAAAAAAAAAAAEAAQAAAAcAAAQAAAEAAQABBAAAAQABAAIEsYcWAAAAAQixhwcAFgAGCAAAAQCOIwAMAAABAHl/BAwAAAEAKSNXR1MgODQgLyBVVE0gem9uZSAzM058V0dTIDg0fAA8R0RBTE1ldGFkYXRhPgogIDxJdGVtIG5hbWU9Ik9GRlNFVCIgc2FtcGxlPSIwIiByb2xlPSJvZmZzZXQiPjA8L0l0ZW0+CiAgPEl0ZW0gbmFtZT0iU0NBTEUiIHNhbXBsZT0iMCIgcm9sZT0ic2NhbGUiPjAuMTAwMDAwMDAwMDAwMDAwMDE8L0l0ZW0+CiAgPEl0ZW0gbmFtZT0iVU5JVFRZUEUiIHNhbXBsZT0iMCIgcm9sZT0idW5pdHR5cGUiPkNlbHNpdXM8L0l0ZW0+CjwvR0RBTE1ldGFkYXRhPgoA";
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
            sizeof(NativeRasterOptions).ShouldBe(56);
            Marshal.OffsetOf<NativeRasterOptions>(nameof(NativeRasterOptions.Flags)).ToInt32().ShouldBe(4);
            Marshal.OffsetOf<NativeRasterOptions>(nameof(NativeRasterOptions.Nodata)).ToInt32().ShouldBe(8);
            Marshal.OffsetOf<NativeRasterOptions>(nameof(NativeRasterOptions.Formula)).ToInt32().ShouldBe(48);
        }
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

    // libddb 2.2 applies the options at a pixel (DDBGetRasterPointValuePixelEx): the NoData override of 130 leaves the
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

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
}
