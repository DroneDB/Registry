using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Registry.Common;
using Registry.Ports.DroneDB.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Managers;

namespace Registry.Web.Test
{
    [TestFixture]
    public class DdbFactoryTest
    {
        private Mock<IOptions<AppSettings>> _appSettingsMock;
        private Logger<DdbManager> _ddbFactoryLogger;

        private const string TestDataFolder = @"Data/Ddb";
        private const string DdbTestDataFolder = @"Data/DdbTest";

        private const string DbTest1ArchiveUrl = "https://github.com/DroneDB/test_data/raw/master/registry/DdbFactoryTest/testdb2.zip";

        private readonly Guid _datasetGuid = Guid.Parse("0a223495-84a0-4c15-b425-c7ef88110e75");

        [SetUp]
        public void Setup()
        {
            _appSettingsMock = new Mock<IOptions<AppSettings>>();
            _ddbFactoryLogger = new Logger<DdbManager>(LoggerFactory.Create(builder => builder.AddConsole()));

            _settings.DatasetsPath = TestDataFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);

        }

        [Test]
        public void Ctor_ExistingDatabase_Ok()
        {

            var factory = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger);

            var ddb = factory.Get(MagicStrings.PublicOrganizationSlug, _datasetGuid);

            ddb.Should().NotBeNull();
        }

        [Test]
        public void Ctor_MissingDatabase_NoException()
        {
            var factory = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger);

            factory.Invoking(x => x.Get("vlwefwef", _datasetGuid))
                .Should().NotThrow<IOException>();

        }

        [Test]
        public void Search_MissingEntry_Empty()
        {

            using var fs = new TestFS(DbTest1ArchiveUrl, nameof(DdbFactoryTest));

            _settings.DatasetsPath = fs.TestFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);

            var factory = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger);

            var ddb = factory.Get(MagicStrings.PublicOrganizationSlug, _datasetGuid);

            var res = ddb.Search("asasdadas.jpg");

            res.Should().BeEmpty();

        }


        [Test]
        public void Search_ExistingEntry_Entry1()
        {

            using var fs = new TestFS(DbTest1ArchiveUrl, nameof(DdbFactoryTest));

            _settings.DatasetsPath = fs.TestFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);

            const string fileName = "Sub/20200610_144436.jpg";
            const int expectedDepth = 1;
            const int expectedSize = 8248241;
            const EntryType expectedType = EntryType.GeoImage;
            const string expectedHash = "f27ddc96daf9aeff3c026de8292681296c3e9d952b647235878c50f2b7b39e94";
            var expectedModifiedTime = new DateTime(2020, 06, 10, 14, 44, 36);
            var expectedMeta = JsonConvert.DeserializeObject<Dictionary<string, object>>(
                "{\"captureTime\":1591800276004.8,\"focalLength\":4.16,\"focalLength35\":26.0,\"height\":3024,\"make\":\"samsung\",\"model\":\"SM-G950F\",\"orientation\":1,\"sensor\":\"samsung sm-g950f\",\"sensorHeight\":4.32,\"sensorWidth\":5.76,\"width\":4032}");
            const double expectedLatitude = 45.50027;
            const double expectedLongitude = 10.60667;
            const double expectedAltitude = 141;

            var factory = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger);

            var ddb = factory.Get(MagicStrings.PublicOrganizationSlug, _datasetGuid);

            var list = ddb.Search(fileName).ToArray();

            list.Should().HaveCount(1);

            var res = list.First();

            res.Path.Should().Be(fileName);
            // TODO: Handle different timezones
            res.ModifiedTime.Should().BeCloseTo(expectedModifiedTime, new TimeSpan(6, 0, 0));
            res.Hash.Should().Be(expectedHash);
            res.Depth.Should().Be(expectedDepth);
            res.Size.Should().Be(expectedSize);
            res.Type.Should().Be(expectedType);
            res.Properties.Should().BeEquivalentTo(expectedMeta);

            res.PointGeometry["geometry"]["coordinates"][0].Value<double>().Should().BeApproximately(expectedLongitude, 0.00001);
            res.PointGeometry["geometry"]["coordinates"][1].Value<double>().Should().BeApproximately(expectedLatitude, 0.00001);
            res.PointGeometry["geometry"]["coordinates"][2].Value<double>().Should().BeApproximately(expectedAltitude, 0.1);

        }

        [Test]
        public void Search_ExistingEntry_Entry2()
        {

            using var fs = new TestFS(DbTest1ArchiveUrl, nameof(DdbFactoryTest));

            _settings.DatasetsPath = fs.TestFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);

            const string fileName = "DJI_0022.JPG";
            const int expectedDepth = 0;
            const int expectedSize = 3872682;
            const EntryType expectedType = EntryType.GeoImage;
            const string expectedHash = "e6e57187a33951a27f51e3a86cc66c6ce43d555f0d51ba3c715fc7b707ce1477";
            var expectedModifiedTime = new DateTime(2017, 04, 2, 20, 01, 27);
            var expectedMeta = JsonConvert.DeserializeObject<Dictionary<string, object>>(
                "{\"cameraPitch\":-90.0,\"cameraRoll\":0.0,\"cameraYaw\":45.29999923706055,\"captureTime\":1466699547000.0,\"focalLength\":3.4222222222222225,\"focalLength35\":20.0,\"height\":2250,\"make\":\"DJI\",\"model\":\"FC300S\",\"orientation\":1,\"sensor\":\"dji fc300s\",\"sensorHeight\":3.4650000000000003,\"sensorWidth\":6.16,\"width\":4000}");
            const double expectedLatitude = 46.842952;
            const double expectedLongitude = -91.994052;
            const double expectedAltitude = 198.51;

            List<List<double>> expectedCoordinates = new List<List<double>>();
            expectedCoordinates.Add(new List<double> { -91.99418833907131, 46.843311240786406, 158.51 });
            expectedCoordinates.Add(new List<double> { -91.99457061482893, 46.843058237783886, 158.51 });
            expectedCoordinates.Add(new List<double> { -91.99391510716002, 46.842591925708966, 158.51 });
            expectedCoordinates.Add(new List<double> { -91.99353283170487, 46.842844926544224, 158.51 });
            expectedCoordinates.Add(new List<double> { -91.99418833907131, 46.843311240786406, 158.51 });

            var factory = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger);

            var ddb = factory.Get(MagicStrings.PublicOrganizationSlug, _datasetGuid);

            var list = ddb.Search(fileName).ToArray();

            list.Should().HaveCount(1);

            var res = list.First();

            res.Path.Should().Be(fileName);
            // TODO: Handle different timezones
            res.ModifiedTime.Should().BeCloseTo(expectedModifiedTime, new TimeSpan(6, 0, 0));
            res.Hash.Should().Be(expectedHash);
            res.Depth.Should().Be(expectedDepth);
            res.Size.Should().Be(expectedSize);
            res.Type.Should().Be(expectedType);
            res.Properties.Should().BeEquivalentTo(expectedMeta);
            res.PointGeometry["geometry"]["coordinates"][0].Value<double>().Should().BeApproximately(expectedLongitude, 0.00001);
            res.PointGeometry["geometry"]["coordinates"][1].Value<double>().Should().BeApproximately(expectedLatitude, 0.00001);
            res.PointGeometry["geometry"]["coordinates"][2].Value<double>().Should().BeApproximately(expectedAltitude, 0.1);

            var polygon = res.PolygonGeometry;

            var coords = polygon["geometry"]["coordinates"][0];
            for (int i = 0; i < expectedCoordinates.Count; i++)
            {   
                for (int j = 0; j < expectedCoordinates[i].Count; j++)
                {
                    coords[i][j].Value<double>().Should().BeApproximately(expectedCoordinates[i][j], 0.001);
                }
            }
        }


        [Test]
        public void Add_RemoveImageAddNewImage_Ok()
        {

            using var fs = new TestFS(DbTest1ArchiveUrl, nameof(DdbFactoryTest));

            _settings.DatasetsPath = fs.TestFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);

            var factory = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger);

            var ddb = factory.Get(MagicStrings.PublicOrganizationSlug, _datasetGuid);

            const string fileName = "DJI_0028.JPG";

            ddb.Remove(fileName);

            var path = Path.Combine(TestDataFolder, fileName);

            if (!File.Exists(path))
                CommonUtils.SmartDownloadFile("https://github.com/DroneDB/test_data/raw/master/test-datasets/drone_dataset_brighton_beach/" + fileName, path);

            ddb.Add(fileName, File.ReadAllBytes(path));

            ddb.Search(fileName).Should().HaveCount(1);

        }

        #region Test Data

        private readonly AppSettings _settings = JsonConvert.DeserializeObject<AppSettings>(@"{
    ""Secret"": ""a2780070a24cfcaf5a4a43f931200ba0d19d8b86b3a7bd5123d9ad75b125f480fcce1f9b7f41a53abe2ba8456bd142d38c455302e0081e5139bc3fc9bf614497"",
    ""TokenExpirationInDays"": 7,
    ""RevokedTokens"": [
      """"
    ],
    ""AuthProvider"": ""Sqlite"",
    ""RegistryProvider"": ""Sqlite"",
    ""StorageProvider"": {
      ""type"": ""Physical"",
      ""settings"": {
        ""path"": ""./temp""
      }
    },
    ""DefaultAdmin"": {
      ""Email"": ""admin@example.com"",
      ""UserName"": ""admin"",
      ""Password"": ""password""
    },
    ""DdbStoragePath"": ""./Data/Ddb"",
    ""DdbPath"": ""./ddb""

  }
  ");

        #endregion

    }
}
