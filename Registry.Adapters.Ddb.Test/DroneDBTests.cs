using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Newtonsoft.Json;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Common.Test;
using Registry.Ports.DroneDB.Models;

namespace Registry.Adapters.Ddb.Test
{
    public class DDBWrapperTests
    {
        private const string BaseTestFolder = nameof(DDBWrapperTests);
        private const string TestFileUrl =
            "https://github.com/DroneDB/test_data/raw/master/test-datasets/drone_dataset_brighton_beach/DJI_0023.JPG";
        private const string Test1ArchiveUrl = "https://github.com/DroneDB/test_data/raw/master/registry/DdbFactoryTest/testdb1.zip";
        private const string Test3ArchiveUrl = "https://github.com/DroneDB/test_data/raw/master/ddb-test/Test3.zip";

        private const string DdbFolder = ".ddb";

        private const string TestGeoTiffUrl =
            "https://github.com/DroneDB/test_data/raw/master/brighton/odm_orthophoto.tif";

        private const string TestDelta1ArchiveUrl = "https://github.com/DroneDB/test_data/raw/master/delta/first.zip";
        private const string TestDelta2ArchiveUrl = "https://github.com/DroneDB/test_data/raw/master/delta/second.zip";

        private const string TestPointCloudUrl =
            "https://github.com/DroneDB/test_data/raw/master/brighton/point_cloud.laz";

        [SetUp]
        public void Setup()
        {
            DDBWrapper.RegisterProcess(true);
        }

        [Test]
        public void GetVersion_HasValue()
        {
            Assert.IsTrue(DDBWrapper.GetVersion().Length > 0, "Can call GetVersion()");
        }

        [Test]
        public void Init_NonExistant_Exception()
        {
            Action act = () => DDBWrapper.Init("nonexistant");
            act.Should().Throw<DDBException>();

            act = () => DDBWrapper.Init(null);
            act.Should().Throw<DDBException>();

        }

        [Test]
        public void Init_EmptyFolder_Ok()
        {

            const string folder = "testInit";

            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);

            DDBWrapper.Init(folder).Should().Contain(folder);
            Directory.Exists(Path.Join(folder, ".ddb")).Should().BeTrue();
        }

        [Test]
        public void Add_NonExistant_Exception()
        {
            Action act = () => DDBWrapper.Add("nonexistant", "");
            act.Should().Throw<DDBException>();

            act = () => DDBWrapper.Add("nonexistant", "test");
            act.Should().Throw<DDBException>();

            act = () => DDBWrapper.Add(null, "test");
            act.Should().Throw<DDBException>();

            act = () => DDBWrapper.Add("nonexistant", (string)null);
            act.Should().Throw<DDBException>();

        }

        [Test]
        public void EndToEnd_Add_Remove()
        {

            const string testFolder = "testAdd";

            if (Directory.Exists(testFolder)) Directory.Delete(testFolder, true);

            Directory.CreateDirectory(testFolder);
            DDBWrapper.Init(testFolder);

            File.WriteAllText(Path.Join(testFolder, "file.txt"), "test");
            File.WriteAllText(Path.Join(testFolder, "file2.txt"), "test");
            File.WriteAllText(Path.Join(testFolder, "file3.txt"), "test");

            Assert.Throws<DDBException>(() => DDBWrapper.Add(testFolder, "invalid"));

            var entry = DDBWrapper.Add(testFolder, Path.Join(testFolder, "file.txt"))[0];
            entry.Path.Should().Be("file.txt");
            entry.Hash.Should().NotBeNullOrWhiteSpace();

            var entries = DDBWrapper.Add(testFolder, new[] { Path.Join(testFolder, "file2.txt"), Path.Join(testFolder, "file3.txt") });
            entries.Should().HaveCount(2);

            DDBWrapper.Remove(testFolder, Path.Combine(testFolder, "file.txt"));

            Assert.Throws<DDBException>(() => DDBWrapper.Remove(testFolder, "invalid"));
        }

        [Test]
        public void Info_InvalidFile_Exception()
        {
            Action act = () => DDBWrapper.Info("invalid");
            act.Should().Throw<DDBException>();

            act = () => DDBWrapper.Info((string)null);
            act.Should().Throw<DDBException>();
        }

        [Test]
        public void Info_GenericFile_Details()
        {

            const string testFolder = "testInfo";

            if (Directory.Exists(testFolder)) Directory.Delete(testFolder, true);
            Directory.CreateDirectory(testFolder);

            File.WriteAllText(Path.Join(testFolder, "file.txt"), "test");
            File.WriteAllText(Path.Join(testFolder, "file2.txt"), "test");

            var e = DDBWrapper.Info(Path.Join(testFolder, "file.txt"), withHash: true)[0];
            Assert.IsNotEmpty(e.Hash);

            // TODO: troubleshoot this and use 
            var es = DDBWrapper.Info(testFolder, true);
            Assert.AreEqual(2, es.Count);
            Assert.AreEqual(EntryType.Generic, es[0].Type);
            Assert.IsTrue(es[0].Size > 0);
            Assert.AreEqual(DateTime.Now.Year, es[0].ModifiedTime.Year);
        }

        [Test]
        public void Add_ImageFile_Ok()
        {

            using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

            var ddbPath = Path.Combine(test.TestFolder, "public", "default");

            using var tempFile = new TempFile(TestFileUrl, BaseTestFolder);

            DDBWrapper.Remove(ddbPath, Path.Combine(ddbPath, "DJI_0023.JPG"));

            var destPath = Path.Combine(ddbPath, Path.GetFileName(tempFile.FilePath));

            File.Move(tempFile.FilePath, destPath);

            var res = DDBWrapper.Add(ddbPath, destPath);

            res.Count.Should().Be(1);

        }

        [Test]
        public void Info_ImageFile_Details()
        {

            //var expectedMeta = JsonConvert.DeserializeObject<Dictionary<string, string>>(
            //    @"{""cameraPitch"":""-89.9000015258789"",""cameraRoll"":""0.0"",""cameraYaw"":""43.79999923706055"",""captureTime"":""1466699554000.0"",""focalLength"":""3.4222222222222225"",""focalLength35"":""20.0"",""height"":""2250"",""make"":""DJI"",""model"":""FC300S"",""orientation"":""1"",""sensor"":""dji fc300s"",""sensorHeight"":""3.4650000000000003"",""sensorWidth"":""6.16"",""width"":""4000""}");

            using var tempFile = new TempFile(TestFileUrl, BaseTestFolder);

            var res = DDBWrapper.Info(tempFile.FilePath, withHash: true);

            res.Should().NotBeNull();
            res.Should().HaveCount(1);

            var info = res.First();

            // Just check some fields
            //info.Meta.Should().BeEquivalentTo(expectedMeta);

            info.Properties.Should().NotBeEmpty();
            info.Properties.Should().HaveCount(14);
            info.Properties["make"].Should().Be("DJI");
            info.Properties["model"].Should().Be("FC300S");
            info.Properties["sensor"].Should().Be("dji fc300s");
            info.Hash.Should().Be("246fed68dec31b17dc6d885cee10a2c08f2f1c68901a8efa132c60bdb770e5ff");
            info.Type.Should().Be(EntryType.GeoImage);
            info.Size.Should().Be(3876862);
            // We can ignore this
            // info.Depth.Should().Be(0);
            info.PointGeometry.Should().NotBeNull();
            info.PolygonGeometry.Should().NotBeNull();

        }

        [Test]
        public void List_Nonexistant_Exception()
        {
            Action act = () => DDBWrapper.List("invalid", "");
            act.Should().Throw<DDBException>();

            act = () => DDBWrapper.List("invalid", "wefrfwef");
            act.Should().Throw<DDBException>();

            act = () => DDBWrapper.List(null, "wefrfwef");
            act.Should().Throw<DDBException>();

            act = () => DDBWrapper.List("invalid", (string)null);
            act.Should().Throw<DDBException>();

        }

        [Test]
        public void List_ExistingFileSubFolder_Ok()
        {
            using var fs = new TestFS(Test1ArchiveUrl, nameof(DDBWrapperTests));

            const string fileName = "Sub/20200610_144436.jpg";
            const int expectedDepth = 1;
            const int expectedSize = 8248241;
            var expectedType = EntryType.GeoImage;
            const string expectedHash = "f27ddc96daf9aeff3c026de8292681296c3e9d952b647235878c50f2b7b39e94";
            var expectedModifiedTime = new DateTime(2020, 06, 10, 14, 44, 36);
            var expectedMeta = JsonConvert.DeserializeObject<Dictionary<string, object>>(
                "{\"captureTime\":1591800276004.8,\"focalLength\":4.16,\"focalLength35\":26.0,\"height\":3024,\"make\":\"samsung\",\"model\":\"SM-G950F\",\"orientation\":1,\"sensor\":\"samsung sm-g950f\",\"sensorHeight\":4.32,\"sensorWidth\":5.76,\"width\":4032}");
            //const double expectedLatitude = 45.50027;
            //const double expectedLongitude = 10.60667;
            //const double expectedAltitude = 141;


            var ddbPath = Path.Combine(fs.TestFolder, "public", "default");

            var res = DDBWrapper.List(ddbPath, Path.Combine(ddbPath, fileName));

            res.Should().HaveCount(1);

            var file = res.First();

            file.Path.Should().Be(fileName);
            // TODO: Handle different timezones
            file.ModifiedTime.Should().BeCloseTo(expectedModifiedTime, new TimeSpan(6, 0, 0));
            file.Hash.Should().Be(expectedHash);
            file.Depth.Should().Be(expectedDepth);
            file.Size.Should().Be(expectedSize);
            file.Type.Should().Be(expectedType);
            file.Properties.Should().BeEquivalentTo(expectedMeta);
            file.PointGeometry.Should().NotBeNull();
            //file.PointGeometry.Coordinates.Latitude.Should().BeApproximately(expectedLatitude, 0.00001);
            //file.PointGeometry.Coordinates.Longitude.Should().BeApproximately(expectedLongitude, 0.00001);
            //file.PointGeometry.Coordinates.Altitude.Should().Be(expectedAltitude);

        }

        [Test]
        public void List_ExistingFile_Ok()
        {
            using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

            var ddbPath = Path.Combine(test.TestFolder, "public", "default");

            var res = DDBWrapper.List(ddbPath, Path.Combine(ddbPath, "DJI_0027.JPG"));

            res.Should().HaveCount(1);
            var entry = res.First();

            Entry expectedEntry = JsonConvert.DeserializeObject<Entry>(
                "{\"depth\":0,\"hash\":\"3157958dd4f2562c8681867dfd6ee5bf70b6e9595b3e3b4b76bbda28342569ed\",\"properties\":{\"cameraPitch\":-89.9000015258789,\"cameraRoll\":0.0,\"cameraYaw\":-131.3000030517578,\"captureTime\":1466699584000.0,\"focalLength\":3.4222222222222225,\"focalLength35\":20.0,\"height\":2250,\"make\":\"DJI\",\"model\":\"FC300S\",\"orientation\":1,\"sensor\":\"dji fc300s\",\"sensorHeight\":3.4650000000000003,\"sensorWidth\":6.16,\"width\":4000},\"mtime\":1491156087,\"path\":\"DJI_0027.JPG\",\"point_geom\":{\"crs\":{\"properties\":{\"name\":\"EPSG:4326\"},\"type\":\"name\"},\"geometry\":{\"coordinates\":[-91.99408299999999,46.84260499999999,198.5099999999999],\"type\":\"Point\"},\"properties\":{},\"type\":\"Feature\"},\"polygon_geom\":{\"crs\":{\"properties\":{\"name\":\"EPSG:4326\"},\"type\":\"name\"},\"geometry\":{\"coordinates\":[[[-91.99397836402999,46.8422402913,158.5099999999999],[-91.99357489543,46.84247729175999,158.5099999999999],[-91.99418894036,46.84296945989999,158.5099999999999],[-91.99459241001999,46.8427324573,158.5099999999999],[-91.99397836402999,46.8422402913,158.5099999999999]]],\"type\":\"Polygon\"},\"properties\":{},\"type\":\"Feature\"},\"size\":3185449,\"type\":3}");

            entry.Should().BeEquivalentTo(expectedEntry);

        }

        [Test]
        public void List_AllFiles_Ok()
        {
            using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

            var ddbPath = Path.Combine(test.TestFolder, "public", "default");

            var res = DDBWrapper.List(ddbPath, Path.Combine(ddbPath, "."), true);

            res.Should().HaveCount(26);

            res = DDBWrapper.List(ddbPath, ddbPath, true);

            res.Should().HaveCount(26);

        }


        [Test]
        public void Remove_Nonexistant_Exception()
        {
            Action act = () => DDBWrapper.Remove("invalid", "");
            act.Should().Throw<DDBException>();

            act = () => DDBWrapper.Remove("invalid", "wefrfwef");
            act.Should().Throw<DDBException>();

            act = () => DDBWrapper.Remove(null, "wefrfwef");
            act.Should().Throw<DDBException>();

        }


        [Test]
        public void Remove_ExistingFile_Ok()
        {
            using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

            const string fileName = "DJI_0027.JPG";

            var ddbPath = Path.Combine(test.TestFolder, "public", "default");

            var res = DDBWrapper.List(ddbPath, Path.Combine(ddbPath, fileName));
            res.Should().HaveCount(1);

            DDBWrapper.Remove(ddbPath, Path.Combine(ddbPath, fileName));

            res = DDBWrapper.List(ddbPath, Path.Combine(ddbPath, fileName));
            res.Should().HaveCount(0);

        }

        [Test]
        public void Remove_AllFiles_Ok()
        {
            using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

            const string fileName = ".";

            var ddbPath = Path.Combine(test.TestFolder, "public", "default");

            DDBWrapper.Remove(ddbPath, Path.Combine(ddbPath, fileName));

            var res = DDBWrapper.List(ddbPath, ".", true);
            res.Should().HaveCount(0);

        }

        [Test]
        public void Remove_NonexistantFile_Exception()
        {
            using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

            const string fileName = "elaiuyhrfboeawuyirgfb";

            var ddbPath = Path.Combine(test.TestFolder, "public", "default");

            Action act = () => DDBWrapper.Remove(ddbPath, Path.Combine(ddbPath, fileName));

            act.Should().Throw<DDBException>();
        }

        [Test]
        public void Entry_Deserialization_Ok()
        {
            string json = "{'hash': 'abc', 'mtime': 5}";
            Entry e = JsonConvert.DeserializeObject<Entry>(json);
            Assert.IsTrue(e.ModifiedTime.Year == 1970);
        }



        [Test]
        public void Password_HappyPath_Ok()
        {

            using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

            var ddbPath = Path.Combine(test.TestFolder, "public", "default");

            DDBWrapper.VerifyPassword(ddbPath, string.Empty).Should().BeTrue();

            DDBWrapper.AppendPassword(ddbPath, "testpassword");

            DDBWrapper.VerifyPassword(ddbPath, "testpassword").Should().BeTrue();
            DDBWrapper.VerifyPassword(ddbPath, "wrongpassword").Should().BeFalse();

            DDBWrapper.ClearPasswords(ddbPath);
            DDBWrapper.VerifyPassword(ddbPath, "testpassword").Should().BeFalse();


        }

        [Test]
        public void Chaddr_HappyPath_Ok()
        {

            using var test = new TestFS(Test3ArchiveUrl, BaseTestFolder);

            var ddbPath = Path.Combine(test.TestFolder);

            var res = DDBWrapper.ChangeAttributes(ddbPath, new Dictionary<string, object> { { "public", true } });

            res["public"].Should().Be(true);

            res = DDBWrapper.ChangeAttributes(ddbPath, new Dictionary<string, object> { { "public", false } });

            res["public"].Should().Be(false);

        }

        [Test]
        public void Chaddr_NullAttr_Exception()
        {

            using var test = new TestFS(Test3ArchiveUrl, BaseTestFolder);

            var ddbPath = test.TestFolder;

            Action act = () => DDBWrapper.ChangeAttributes(ddbPath, null);

            act.Should().Throw<ArgumentException>();

        }

        [Test]
        public void GenerateThumbnail_HappyPath_Ok()
        {

            using var tempFile = new TempFile(TestFileUrl, BaseTestFolder);

            var destPath = Path.Combine(Path.GetTempPath(), "test.jpg");//Path.GetTempFileName();

            try
            {
                DDBWrapper.GenerateThumbnail(tempFile.FilePath, 300, destPath);

                var info = new FileInfo(destPath);
                info.Exists.Should().BeTrue();
                info.Length.Should().BeGreaterThan(0);

            }
            finally
            {
                if (File.Exists(destPath)) File.Delete(destPath);
            }
        }

        [Test]
        public void GenerateMemoryThumbnail_HappyPath_Ok()
        {
            using var tempFile = new TempFile(TestFileUrl, BaseTestFolder);
            var buffer = DDBWrapper.GenerateThumbnail(tempFile.FilePath, 300);
            buffer.Length.Should().BeGreaterThan(0);
        }

        [Test]
        public void GenerateTile_HappyPath_Ok()
        {

            using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);

            var destPath = Path.Combine(Path.GetTempPath(), "test.jpg");

            try
            {
                var path = DDBWrapper.GenerateTile(tempFile.FilePath, 18, 64083, 92370, 256, true);
            }
            finally
            {
                if (File.Exists(destPath)) File.Delete(destPath);
            }
        }

        [Test]
        public void GenerateMemoryTile_HappyPath_Ok()
        {
            using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);

            var buffer = DDBWrapper.GenerateMemoryTile(tempFile.FilePath, 18, 64083, 92370, 256, true);
            buffer.Length.Should().BeGreaterThan(0);
        }

        [Test]
        public void Tag_HappyPath_Ok()
        {

            const string goodTag = "pippo/pluto";
            const string goodTagWithRegistry = "https://test.com/pippo/pluto";

            using var test = new TestFS(Test3ArchiveUrl, BaseTestFolder);

            var ddbPath = Path.Combine(test.TestFolder, DdbFolder);

            var tag = DDBWrapper.GetTag(ddbPath);

            tag.Should().BeNull();

            DDBWrapper.SetTag(ddbPath, goodTag);

            tag = DDBWrapper.GetTag(ddbPath);

            tag.Should().Be(goodTag);

            DDBWrapper.SetTag(ddbPath, goodTagWithRegistry);

            tag = DDBWrapper.GetTag(ddbPath);

            tag.Should().Be(goodTagWithRegistry);

        }

        [Test]
        public void Tag_ErrorCases_Ok()
        {

            const string badTag = "pippo";
            const string badTag2 = "����+���+�AAadff_-.-.,";

            using var test = new TestFS(Test3ArchiveUrl, BaseTestFolder);

            var ddbPath = Path.Combine(test.TestFolder, DdbFolder);

            Action act = () => DDBWrapper.SetTag(ddbPath, badTag);

            act.Should().Throw<DDBException>();

            act = () => DDBWrapper.SetTag(ddbPath, badTag2);

            act.Should().Throw<DDBException>();

            act = () => DDBWrapper.SetTag(ddbPath, string.Empty);

            act.Should().Throw<DDBException>();

            act = () => DDBWrapper.SetTag(ddbPath, null);

            act.Should().Throw<ArgumentException>();

        }

        [Test]
        public void Stamp_HappyPath_Ok()
        {
            using var test = new TestFS(Test3ArchiveUrl, BaseTestFolder);

            var ddbPath = Path.Combine(test.TestFolder, DdbFolder);

            var stamp = DDBWrapper.GetStamp(ddbPath);
            stamp.Checksum.Should().NotBeNull();
            stamp.Entries.Count.Should().BeGreaterThan(0);
        }

        [Test]
        public void Delta_HappyPath_Ok()
        {
            using var source = new TestFS(TestDelta2ArchiveUrl, BaseTestFolder);
            using var destination = new TestFS(TestDelta1ArchiveUrl, BaseTestFolder);

            var delta = DDBWrapper.Delta(source.TestFolder, destination.TestFolder);

            delta.Adds.Length.Should().BeGreaterThan(0);
            delta.Removes.Length.Should().BeGreaterThan(0);

        }

        [Test]
        public void MoveEntry_SimpleRename_Ok()
        {
            using var test = new TestFS(TestDelta2ArchiveUrl, BaseTestFolder);

            DDBWrapper.MoveEntry(test.TestFolder, "plutone.txt", "test.txt");

            var res = DDBWrapper.List(test.TestFolder, test.TestFolder, true);

            res.Should().HaveCount(11);
            res[8].Path.Should().Be("test.txt");
        }

        [Test]
        public void Build_SimpleBuild_Ok()
        {

            using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

            var ddbPath = Path.Combine(test.TestFolder, "public", "default");

            using var tempFile = new TempFile(TestPointCloudUrl, BaseTestFolder);

            var destPath = Path.Combine(ddbPath, Path.GetFileName(tempFile.FilePath));

            File.Move(tempFile.FilePath, destPath);

            var res = DDBWrapper.Add(ddbPath, destPath);

            res.Count.Should().Be(1);

            DDBWrapper.Build(ddbPath);

        }

        [Test]
        public void IsBuildable_PointCloud_True()
        {

            using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

            var ddbPath = Path.Combine(test.TestFolder, "public", "default");

            using var tempFile = new TempFile(TestPointCloudUrl, BaseTestFolder);

            var destPath = Path.Combine(ddbPath, Path.GetFileName(tempFile.FilePath));

            File.Move(tempFile.FilePath, destPath);

            var res = DDBWrapper.Add(ddbPath, destPath);

            res.Count.Should().Be(1);

            DDBWrapper.IsBuildable(ddbPath, Path.GetFileName(destPath)).Should().BeTrue();

        }

        [Test]
        public void IsBuildable_TextFile_False()
        {

            using var test = new TestFS(TestDelta2ArchiveUrl, BaseTestFolder);

            DDBWrapper.IsBuildable(test.TestFolder, "lol.txt").Should().BeFalse();

        }

        [Test]
        public void MetaAdd_Ok()
        {
            using var area = new TestArea("metaAddOkTest");
            DDBWrapper.Init(area.TestFolder);

            FluentActions.Invoking(() => DDBWrapper.MetaAdd(area.TestFolder, "test", "123")).Should()
                .Throw<DDBException>(); // Needs plural key
            // DDBWrapper.MetaAdd("metaAddTest", "", "tests", "123").Data.ToObject<int>().Should().Be(123);
        }

        [Test]
        public void MetaAdd_Json()
        {
            using var area = new TestArea("metaAddJsonTest");
            DDBWrapper.Init(area.TestFolder);

            var res = DDBWrapper.MetaAdd(area.TestFolder, "tests", "{\"test\": true}");
            JsonConvert.SerializeObject(res.Data).Should().Be("{\"test\":true}");
            res.Id.Should().NotBeNull();
            res.ModifiedTime.Should().BeCloseTo(DateTime.UtcNow, new TimeSpan(0,0,1));
        }

        [Test]
        public void MetaSet_Ok()
        {
            using var area = new TestArea("metaSetOkTest");
            DDBWrapper.Init(area.TestFolder);

            var f = Path.Join(area.TestFolder, "test.txt");
            File.WriteAllText(f, null);
            
            DDBWrapper.Add(area.TestFolder, f);

            FluentActions.Invoking(() => DDBWrapper.MetaSet(area.TestFolder, "tests", "123", f)).Should()
                .Throw<DDBException>(); // Needs singular key

            DDBWrapper.MetaSet(area.TestFolder, "test", "abc", f).Data.ToObject<string>().Should().Be("abc");
            DDBWrapper.MetaSet(area.TestFolder, "test", "efg", f).Data.ToObject<string>().Should().Be("efg");
        }

        [Test]
        public void MetaRemove_Ok()
        {
            using var area = new TestArea("metaRemoveOkTest");
            DDBWrapper.Init(area.TestFolder);

            var id = DDBWrapper.MetaSet(area.TestFolder, "test", "123").Id;
            DDBWrapper.MetaRemove(area.TestFolder, "invalid").Should().Be(0);
            DDBWrapper.MetaRemove(area.TestFolder, id).Should().Be(1);
            DDBWrapper.MetaRemove(area.TestFolder, id).Should().Be(0);
        }

        [Test]
        public void MetaGet_Ok()
        {
            using var area = new TestArea("metaGetOkTest");
            DDBWrapper.Init(area.TestFolder);

            DDBWrapper.MetaSet(area.TestFolder, "abc", "true");

            FluentActions.Invoking(() => DDBWrapper.MetaGet(area.TestFolder, "nonexistant")).Should()
                .Throw<DDBException>();

            FluentActions.Invoking(() => DDBWrapper.MetaGet(area.TestFolder, "abc", "123")).Should()
                .Throw<DDBException>();

            JsonConvert.DeserializeObject<Meta>(DDBWrapper.MetaGet(area.TestFolder, "abc")).Data.ToObject<bool>()
                .Should().Be(true);
        }

        [Test]
        public void MetaGet_Ok2()
        {
            using var area = new TestArea("metaGetOkTest");
            DDBWrapper.Init(area.TestFolder);

            DDBWrapper.MetaAdd(area.TestFolder, "tests", "{\"test\":true}");
            DDBWrapper.MetaAdd(area.TestFolder, "tests", "{\"test\":false}");
            DDBWrapper.MetaAdd(area.TestFolder, "tests", "{\"test\":null}");

            var res = JsonConvert.DeserializeObject<Meta[]>(DDBWrapper.MetaGet(area.TestFolder, "tests"));

            res.Should().HaveCount(3);

        }

        [Test]
        public void MetaUnset_Ok()
        {
            using var area = new TestArea("metaUnsetOkTest");
            DDBWrapper.Init(area.TestFolder);
            
            var f = Path.Join(area.TestFolder, "test.txt");
            File.WriteAllText(f, null);

            DDBWrapper.Add(area.TestFolder, f);

            DDBWrapper.MetaSet(area.TestFolder, "abc", "[1,2,3]");
            DDBWrapper.MetaUnset(area.TestFolder, "abc", f).Should().Be(0);
            DDBWrapper.MetaUnset(area.TestFolder, "abc").Should().Be(1);
            DDBWrapper.MetaUnset(area.TestFolder, "abc").Should().Be(0);
        }

        [Test]
        public void MetaList_Ok()
        {
            using var area = new TestArea("metaListOkTest");
            DDBWrapper.Init(area.TestFolder);

            DDBWrapper.MetaAdd(area.TestFolder, "annotations", "123");
            DDBWrapper.MetaAdd(area.TestFolder, "examples", "abc");
            DDBWrapper.MetaList(area.TestFolder).Should().HaveCount(2);
        }

        [Test]
        [Explicit("Clean test directory")]
        public void Clean_Domain()
        {
            TempFile.CleanDomain(BaseTestFolder);
            TestFS.ClearCache(BaseTestFolder);
        }
    }
}