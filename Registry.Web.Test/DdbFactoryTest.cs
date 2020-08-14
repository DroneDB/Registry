using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using FluentAssertions;
using GeoAPI.Geometries;
using GeoJSON.Net.Feature;
using GeoJSON.Net.Geometry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Registry.Common;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Test
{
    [TestFixture]
    public class DdbFactoryTest
    {
        private Mock<IOptions<AppSettings>> _appSettingsMock;
        private Logger<DdbFactory> _ddbFactoryLogger;

        private const string TestDataFolder = @"Data/Ddb";
        private const string DdbTestDataFolder = @"Data/DdbTest";
        private const string BaseTestFolder = "DdbFactoryTest";
        private const string DdbFolder = "Ddb";

        private const string Test1ArchiveUrl = "https://digipa.it/wp-content/uploads/2020/08/Test1.zip";

        [SetUp]
        public void Setup()
        {
            _appSettingsMock = new Mock<IOptions<AppSettings>>();
            _ddbFactoryLogger = new Logger<DdbFactory>(LoggerFactory.Create(builder => builder.AddConsole()));

            if (!Directory.Exists(DdbTestDataFolder))
            {
                Directory.CreateDirectory(DdbTestDataFolder);
                File.WriteAllText(Path.Combine(DdbTestDataFolder, "ddbcmd.exe"), string.Empty);
            }

            _settings.DdbPath = DdbTestDataFolder;
            _settings.DdbStoragePath = TestDataFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);

        }

        [Test]
        public void Ctor_ExistingDatabase_Ok()
        {

            var factory = new DdbFactory(_appSettingsMock.Object, _ddbFactoryLogger);

            var ddb = factory.GetDdb(MagicStrings.PublicOrganizationId, MagicStrings.DefaultDatasetSlug);

            ddb.Should().NotBeNull();
        }

        [Test]
        public void Ctor_MissingDatabase_IOException()
        {
            var factory = new DdbFactory(_appSettingsMock.Object, _ddbFactoryLogger);

            factory.Invoking(x => x.GetDdb("vlwefwef", MagicStrings.DefaultDatasetSlug))
                .Should().Throw<IOException>();

        }

        [Test]
        public void Search_MissingEntry_Empty()
        {
            var factory = new DdbFactory(_appSettingsMock.Object, _ddbFactoryLogger);

            var ddb = factory.GetDdb(MagicStrings.PublicOrganizationId, MagicStrings.DefaultDatasetSlug);

            var res = ddb.Search("asasdadas.jpg");

            res.Should().BeEmpty();

        }

        
        [Test]
        public void Search_ExistingEntry_Entry1()
        {

            const string fileName = "Sub/20200610_144436.jpg";
            const int expectedDepth = 1;
            const int expectedSize = 8248241;
            const int expectedType = 3;
            const string expectedHash = "f27ddc96daf9aeff3c026de8292681296c3e9d952b647235878c50f2b7b39e94";
            var expectedModifiedTime = new DateTime(2020, 06, 10, 14, 44, 36);
            var expectedMeta = JsonConvert.DeserializeObject<JObject>(
                "{\"captureTime\":1591800276004.8,\"focalLength\":4.16,\"focalLength35\":26.0,\"height\":3024,\"make\":\"samsung\",\"model\":\"SM-G950F\",\"orientation\":1,\"sensor\":\"samsung sm-g950f\",\"sensorHeight\":4.32,\"sensorWidth\":5.76,\"width\":4032}");
            const double expectedLatitude = 45.50027;
            const double expectedLongitude = 10.60667;
            const double expectedAltitude = 141;

            var factory = new DdbFactory(_appSettingsMock.Object, _ddbFactoryLogger);

            var ddb = factory.GetDdb(MagicStrings.PublicOrganizationId, MagicStrings.DefaultDatasetSlug);
            
            var list = ddb.Search(fileName).ToArray();

            list.Should().HaveCount(1);

            var res = list.First();

            res.Path.Should().Be(fileName);
            res.ModifiedTime.Should().Be(expectedModifiedTime);
            res.Hash.Should().Be(expectedHash);
            res.Depth.Should().Be(expectedDepth);
            res.Size.Should().Be(expectedSize);
            res.Type.Should().Be(expectedType);
            res.Meta.Should().BeEquivalentTo(expectedMeta);
            res.PointGeometry.Coordinates.Latitude.Should().BeApproximately(expectedLatitude, 0.00001);
            res.PointGeometry.Coordinates.Longitude.Should().BeApproximately(expectedLongitude, 0.00001);
            res.PointGeometry.Coordinates.Altitude.Should().Be(expectedAltitude);

        }

        [Test]
        public void Search_ExistingEntry_Entry2()
        {

            const string fileName = "DJI_0022.JPG";
            const int expectedDepth = 0;
            const int expectedSize = 3872682;
            const int expectedType = 3;
            const string expectedHash = "e6e57187a33951a27f51e3a86cc66c6ce43d555f0d51ba3c715fc7b707ce1477";
            var expectedModifiedTime = new DateTime(2017, 04, 2, 20, 01, 27);
            var expectedMeta = JsonConvert.DeserializeObject<JObject>(
                "{\"cameraPitch\":-90.0,\"cameraRoll\":0.0,\"cameraYaw\":45.29999923706055,\"captureTime\":1466699547000.0,\"focalLength\":3.4222222222222225,\"focalLength35\":20.0,\"height\":2250,\"make\":\"DJI\",\"model\":\"FC300S\",\"orientation\":1,\"sensor\":\"dji fc300s\",\"sensorHeight\":3.4650000000000003,\"sensorWidth\":6.16,\"width\":4000}");
            const double expectedLatitude = 46.842952;
            const double expectedLongitude = -91.994052;
            const double expectedAltitude = 198.51;

            IPosition[] expectedCoordinates = {
                new Position( 46.843311240786406, -91.99418833907131,158.51),
                new Position( 46.843058237783886, -91.99457061482893,158.51),
                new Position( 46.842591925708966, -91.99391510716002,158.51),
                new Position( 46.842844926544224, -91.99353283170487,158.51),
                new Position( 46.843311240786406, -91.99418833907131,158.51),
            };

            var factory = new DdbFactory(_appSettingsMock.Object, _ddbFactoryLogger);

            var ddb = factory.GetDdb(MagicStrings.PublicOrganizationId, MagicStrings.DefaultDatasetSlug);

            var list = ddb.Search(fileName).ToArray();

            list.Should().HaveCount(1);

            var res = list.First();

            res.Path.Should().Be(fileName);
            res.ModifiedTime.Should().Be(expectedModifiedTime);
            res.Hash.Should().Be(expectedHash);
            res.Depth.Should().Be(expectedDepth);
            res.Size.Should().Be(expectedSize);
            res.Type.Should().Be(expectedType);
            res.Meta.Should().BeEquivalentTo(expectedMeta);
            res.PointGeometry.Coordinates.Latitude.Should().BeApproximately(expectedLatitude, 0.00001);
            res.PointGeometry.Coordinates.Longitude.Should().BeApproximately(expectedLongitude, 0.00001);
            res.PointGeometry.Coordinates.Altitude.Should().Be(expectedAltitude);

            var polygon = (Polygon) res.PolygonGeometry.Geometry;
            
            var coords = polygon.Coordinates[0].Coordinates;

            coords.Should().BeEquivalentTo(expectedCoordinates);

        }


        [Test]
        [Ignore("Waiting for sqlite primary key")]
        public void Add_RemoveImageAddNewImage_Ok()
        {

            var factory = new DdbFactory(_appSettingsMock.Object, _ddbFactoryLogger);

            var ddb = factory.GetDdb(MagicStrings.PublicOrganizationId, MagicStrings.DefaultDatasetSlug);

            const string fileName = "DJI_0028.JPG";

            ddb.Remove(fileName);

            var path = Path.Combine(TestDataFolder, fileName);

            if (!File.Exists(path)) {
                using var client = new WebClient();
                client.DownloadFile("https://github.com/pierotofy/drone_dataset_brighton_beach/blob/master/DJI_0028.JPG", path);
            }

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
