using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shouldly;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Managers;
using Registry.Web.Test.Support;

namespace Registry.Web.Test;

[TestFixture]
[Category(TestCategories.Native)]
[Category(TestCategories.Network)]
public class DdbFactoryTest : TestBase
{
    private MutableOptions<AppSettings> _settingsOptions;
    private ILogger<DdbManager> _ddbFactoryLogger;

    private const string TestDataFolder = @"Data/Ddb";
    private const string DdbTestDataFolder = @"Data/DdbTest";

    private static readonly string DbTest1ArchiveUrl = TestData.Url("registry/DdbFactoryTest/testdb2.zip");

    private readonly Guid _datasetGuid = Guid.Parse("0a223495-84a0-4c15-b425-c7ef88110e75");

    private static readonly IDdbWrapper DdbWrapper = new NativeDdbWrapper();

    [SetUp]
    public void Setup()
    {
        _settingsOptions = TestSettings.Mutable();
        _ddbFactoryLogger = CreateTestLogger<DdbManager>();

        _settings.DatasetsPath = TestDataFolder;
        _settingsOptions.Value = _settings;
    }

    [Test]
    public void Ctor_ExistingDatabase_Ok()
    {

        var factory = new DdbManager(TestSettings.Options<StorageOptions>(_settingsOptions), _ddbFactoryLogger, DdbWrapper);

        var ddb = factory.Get(MagicStrings.PublicOrganizationSlug, _datasetGuid);

        ddb.ShouldNotBeNull();
    }

    [Test]
    public void Ctor_MissingDatabase_NoException()
    {
        var factory = new DdbManager(TestSettings.Options<StorageOptions>(_settingsOptions), _ddbFactoryLogger, DdbWrapper);

        Should.NotThrow(() => factory.Get("vlwefwef", _datasetGuid));

    }

    [Test]
    public void Delete_AfterLookups_ClosesPooledConnectionsAndRemovesTheFolder()
    {
        using var fs = new TestFS(DbTest1ArchiveUrl, nameof(DdbFactoryTest));

        _settings.DatasetsPath = fs.TestFolder;
        _settingsOptions.Value = _settings;

        var factory = new DdbManager(TestSettings.Options<StorageOptions>(_settingsOptions), _ddbFactoryLogger, DdbWrapper);
        var ddb = factory.Get(MagicStrings.PublicOrganizationSlug, _datasetGuid);
        ddb.GetEntry("Sub/20200610_144436.jpg").ShouldNotBeNull(); // leaves an idle native connection

        factory.Delete(MagicStrings.PublicOrganizationSlug, _datasetGuid);

        Directory.Exists(ddb.DatasetFolderPath).ShouldBeFalse();
        DdbWrapper.ReleaseConnections(ddb.DatasetFolderPath).ShouldBe(0, "Delete already closed them");
    }

    [Test]
    public void Search_MissingEntry_Empty()
    {

        using var fs = new TestFS(DbTest1ArchiveUrl, nameof(DdbFactoryTest));

        _settings.DatasetsPath = fs.TestFolder;
        _settingsOptions.Value = _settings;

        var factory = new DdbManager(TestSettings.Options<StorageOptions>(_settingsOptions), _ddbFactoryLogger, DdbWrapper);

        var ddb = factory.Get(MagicStrings.PublicOrganizationSlug, _datasetGuid);

        var res = ddb.Search("asasdadas.jpg");

        res.ShouldBeEmpty();

    }


    [Test]
    public void Search_ExistingEntry_Entry1()
    {

        using var fs = new TestFS(DbTest1ArchiveUrl, nameof(DdbFactoryTest));

        _settings.DatasetsPath = fs.TestFolder;
        _settingsOptions.Value = _settings;

        const string fileName = "Sub/20200610_144436.jpg";
        const int expectedDepth = 1;
        const int expectedSize = 8248241;
        const EntryType expectedType = EntryType.GeoImage;
        const string expectedHash = "f27ddc96daf9aeff3c026de8292681296c3e9d952b647235878c50f2b7b39e94";
        var expectedModifiedTime = new DateTime(2020, 06, 10, 14, 44, 36);
        var expectedMeta = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"captureTime\":1591800276004.8,\"focalLength\":4.16,\"focalLength35\":26.0,\"height\":3024,\"make\":\"samsung\",\"model\":\"SM-G950F\",\"orientation\":1,\"sensor\":\"samsung sm-g950f\",\"sensorHeight\":4.32,\"sensorWidth\":5.76,\"width\":4032}");
        const double expectedLatitude = 45.50027;
        const double expectedLongitude = 10.60667;
        const double expectedAltitude = 141;

        var factory = new DdbManager(TestSettings.Options<StorageOptions>(_settingsOptions), _ddbFactoryLogger, DdbWrapper);

        var ddb = factory.Get(MagicStrings.PublicOrganizationSlug, _datasetGuid);

        var list = ddb.Search(fileName).ToArray();

        list.Count().ShouldBe(1);

        var res = list.First();

        res.Path.ShouldBe(fileName);
        // TODO: Handle different timezones
        res.ModifiedTime.ShouldBe(expectedModifiedTime, TimeSpan.FromHours(6));
        res.Hash.ShouldBe(expectedHash);
        res.Depth.ShouldBe(expectedDepth);
        res.Size.ShouldBe(expectedSize);
        res.Type.ShouldBe(expectedType);
        NativeJson.Serialize(res.Properties).ShouldBe(NativeJson.Serialize(expectedMeta));

        res.PointGeometry!.Value.GetProperty("geometry").GetProperty("coordinates")[0].GetDouble().ShouldBe(expectedLongitude, 0.00001);
        res.PointGeometry!.Value.GetProperty("geometry").GetProperty("coordinates")[1].GetDouble().ShouldBe(expectedLatitude, 0.00001);
        res.PointGeometry!.Value.GetProperty("geometry").GetProperty("coordinates")[2].GetDouble().ShouldBe(expectedAltitude, 0.1);

    }

    [Test]
    public void Search_ExistingEntry_Entry2()
    {

        using var fs = new TestFS(DbTest1ArchiveUrl, nameof(DdbFactoryTest));

        _settings.DatasetsPath = fs.TestFolder;
        _settingsOptions.Value = _settings;

        const string fileName = "DJI_0022.JPG";
        const int expectedDepth = 0;
        const int expectedSize = 3872682;
        const EntryType expectedType = EntryType.GeoImage;
        const string expectedHash = "e6e57187a33951a27f51e3a86cc66c6ce43d555f0d51ba3c715fc7b707ce1477";
        var expectedModifiedTime = new DateTime(2017, 04, 2, 20, 01, 27);
        var expectedMeta = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"cameraPitch\":-90.0,\"cameraRoll\":0.0,\"cameraYaw\":45.29999923706055,\"captureTime\":1466699547000.0,\"focalLength\":3.4222222222222225,\"focalLength35\":20.0,\"height\":2250,\"make\":\"DJI\",\"model\":\"FC300S\",\"orientation\":1,\"sensor\":\"dji fc300s\",\"sensorHeight\":3.4650000000000003,\"sensorWidth\":6.16,\"width\":4000}");
        const double expectedLatitude = 46.842952;
        const double expectedLongitude = -91.994052;
        const double expectedAltitude = 198.51;

        List<List<double>> expectedCoordinates =
        [
            [-91.99418833907131, 46.843311240786406, 158.51],
            [-91.99457061482893, 46.843058237783886, 158.51],
            [-91.99391510716002, 46.842591925708966, 158.51],
            [-91.99353283170487, 46.842844926544224, 158.51],
            [-91.99418833907131, 46.843311240786406, 158.51]
        ];

        var factory = new DdbManager(TestSettings.Options<StorageOptions>(_settingsOptions), _ddbFactoryLogger, DdbWrapper);

        var ddb = factory.Get(MagicStrings.PublicOrganizationSlug, _datasetGuid);

        var list = ddb.Search(fileName).ToArray();

        list.Count().ShouldBe(1);

        var res = list.First();

        res.Path.ShouldBe(fileName);
        // TODO: Handle different timezones
        res.ModifiedTime.ShouldBe(expectedModifiedTime, TimeSpan.FromHours(6));
        res.Hash.ShouldBe(expectedHash);
        res.Depth.ShouldBe(expectedDepth);
        res.Size.ShouldBe(expectedSize);
        res.Type.ShouldBe(expectedType);
        NativeJson.Serialize(res.Properties).ShouldBe(NativeJson.Serialize(expectedMeta));
        res.PointGeometry!.Value.GetProperty("geometry").GetProperty("coordinates")[0].GetDouble().ShouldBe(expectedLongitude, 0.00001);
        res.PointGeometry!.Value.GetProperty("geometry").GetProperty("coordinates")[1].GetDouble().ShouldBe(expectedLatitude, 0.00001);
        res.PointGeometry!.Value.GetProperty("geometry").GetProperty("coordinates")[2].GetDouble().ShouldBe(expectedAltitude, 0.1);

        var polygon = res.PolygonGeometry!.Value;

        var coords = polygon.GetProperty("geometry").GetProperty("coordinates")[0];
        for (int i = 0; i < expectedCoordinates.Count; i++)
        {
            for (int j = 0; j < expectedCoordinates[i].Count; j++)
            {
                coords[i][j].GetDouble().ShouldBe(expectedCoordinates[i][j], 0.001);
            }
        }
    }


    [Test]
    public void Add_RemoveImageAddNewImage_Ok()
    {

        using var fs = new TestFS(DbTest1ArchiveUrl, nameof(DdbFactoryTest));

        _settings.DatasetsPath = fs.TestFolder;
        _settingsOptions.Value = _settings;

        var factory = new DdbManager(TestSettings.Options<StorageOptions>(_settingsOptions), _ddbFactoryLogger, DdbWrapper);

        var ddb = factory.Get(MagicStrings.PublicOrganizationSlug, _datasetGuid);

        const string fileName = "DJI_0028.JPG";

        ddb.Remove(fileName);

        var path = Path.Combine(TestDataFolder, fileName);

        if (!File.Exists(path))
            File.Copy(TestDownloadCache.GetOrDownload(TestData.Url("test-datasets/drone_dataset_brighton_beach/" + fileName), TestDownloadCache.SharedFolder), path, true);

        ddb.Add(fileName, File.ReadAllBytes(path));

        ddb.Search(fileName).Count().ShouldBe(1);

    }

    #region Test Data

    private readonly AppSettings _settings = TestSettings.Default;

    #endregion

}