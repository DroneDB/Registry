using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Common.Model;
using Registry.Common.Test;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;

namespace Registry.Adapters.Ddb.Test;

[TestFixture]
public class NativeDdbWrapperTests : TestBase
{
    private const string BaseTestFolder = nameof(NativeDdbWrapperTests);
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

    private const string TestPointCloud2Url =
        "https://github.com/DroneDB/test_data/raw/master/point-clouds/brighton-beach.laz";

    private static readonly IDdbWrapper DdbWrapper = new NativeDdbWrapper(true);

    [OneTimeSetUp]
    public void Setup()
    {

        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            Directory.SetCurrentDirectory(TestContext.CurrentContext.TestDirectory);

            var ddbFolder = CommonUtils.FindDdbFolder();
            if (ddbFolder == null)
                throw new Exception("DDB not found");

            CommonUtils.SetDefaultDllPath(ddbFolder);
        }


        DdbWrapper.RegisterProcess(true);
    }

    [Test]
    public void GetVersion_HasValue()
    {
        DdbWrapper.GetVersion().Length.ShouldBeGreaterThan(0, "Can call GetVersion()");
    }

    [Test]
    public void Init_NonExistant_Exception()
    {
        Action act = () => DdbWrapper.Init("nonexistant");
        Should.Throw<DdbException>(act);

        act = () => DdbWrapper.Init(null);
        Should.Throw<DdbException>(act);

    }

    [Test]
    public void Init_EmptyFolder_Ok()
    {

        using var area = new TestArea(nameof(Init_EmptyFolder_Ok));

        DdbWrapper.Init(area.TestFolder).ShouldContain(area.TestFolder);
        Directory.Exists(Path.Join(area.TestFolder, ".ddb")).ShouldBeTrue();
    }

    [Test]
    public void Add_NonExistant_Exception()
    {
        Action act = () => DdbWrapper.Add("nonexistant", "");
        Should.Throw<DdbException>(act);

        act = () => DdbWrapper.Add("nonexistant", "test");
        Should.Throw<DdbException>(act);

        act = () => DdbWrapper.Add(null, "test");
        Should.Throw<DdbException>(act);

        act = () => DdbWrapper.Add("nonexistant", (string)null);
        Should.Throw<DdbException>(act);

    }

    [Test]
    public void EndToEnd_Add_Remove()
    {

        using var area = new TestArea(nameof(EndToEnd_Add_Remove));

        DdbWrapper.Init(area.TestFolder);

        File.WriteAllText(Path.Join(area.TestFolder, "file.txt"), "test");
        File.WriteAllText(Path.Join(area.TestFolder, "file2.txt"), "test");
        File.WriteAllText(Path.Join(area.TestFolder, "file3.txt"), "test");

        Assert.Throws<DdbException>(() => DdbWrapper.Add(area.TestFolder, "invalid"));

        var entry = DdbWrapper.Add(area.TestFolder, Path.Join(area.TestFolder, "file.txt"))[0];
        entry.Path.ShouldBe("file.txt");
        entry.Hash.ShouldNotBeNullOrWhiteSpace();

        var entries = DdbWrapper.Add(area.TestFolder, [Path.Join(area.TestFolder, "file2.txt"), Path.Join(area.TestFolder, "file3.txt")
        ]);
        entries.Count.ShouldBe(2);

        DdbWrapper.Remove(area.TestFolder, Path.Combine(area.TestFolder, "file.txt"));

    }

    [Test]
    public void Info_GenericFile_Details()
    {

        using var area = new TestArea(nameof(Info_GenericFile_Details));

        File.WriteAllText(Path.Join(area.TestFolder, "file.txt"), "test");
        File.WriteAllText(Path.Join(area.TestFolder, "file2.txt"), "test");

        var e = DdbWrapper.Info(Path.Join(area.TestFolder, "file.txt"), withHash: true)[0];
        e.Hash.ShouldBe("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08");

        var es = DdbWrapper.Info(area.TestFolder, true);
        es.Count.ShouldBe(2);

        es[0].Type.ShouldBe(EntryType.Generic);
        es[0].Size.ShouldBeGreaterThan(0);
        es[0].ModifiedTime.Year.ShouldBe(DateTime.Now.Year);
    }

    [Test]
    public void Add_ImageFile_Ok()
    {

        using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

        var ddbPath = Path.Combine(test.TestFolder, "public", "default");

        using var tempFile = new TempFile(TestFileUrl, BaseTestFolder);

        DdbWrapper.Remove(ddbPath, Path.Combine(ddbPath, "DJI_0023.JPG"));

        var destPath = Path.Combine(ddbPath, Path.GetFileName(tempFile.FilePath));

        File.Move(tempFile.FilePath, destPath);

        var res = DdbWrapper.Add(ddbPath, destPath);

        res.Count.ShouldBe(1);

    }

    [Test]
    public void Info_ImageFile_Details()
    {

        //var expectedMeta = JsonConvert.DeserializeObject<Dictionary<string, string>>(
        //    @"{""cameraPitch"":""-89.9000015258789"",""cameraRoll"":""0.0"",""cameraYaw"":""43.79999923706055"",""captureTime"":""1466699554000.0"",""focalLength"":""3.4222222222222225"",""focalLength35"":""20.0"",""height"":""2250"",""make"":""DJI"",""model"":""FC300S"",""orientation"":""1"",""sensor"":""dji fc300s"",""sensorHeight"":""3.4650000000000003"",""sensorWidth"":""6.16"",""width"":""4000""}");

        using var tempFile = new TempFile(TestFileUrl, BaseTestFolder);

        var res = DdbWrapper.Info(tempFile.FilePath, withHash: true);

        res.ShouldNotBeNull();
        res.Count.ShouldBe(1);

        var info = res.First();

        // Just check some fields
        //info.Meta.Should().BeEquivalentTo(expectedMeta);

        info.Properties.ShouldNotBeEmpty();
        info.Properties.Count.ShouldBe(15);
        info.Properties["make"].ShouldBe("DJI");
        info.Properties["model"].ShouldBe("FC300S");
        info.Properties["sensor"].ShouldBe("dji fc300s");
        info.Hash.ShouldBe("246fed68dec31b17dc6d885cee10a2c08f2f1c68901a8efa132c60bdb770e5ff");
        info.Type.ShouldBe(EntryType.GeoImage);
        info.Size.ShouldBe(3876862);
        // We can ignore this
        // info.Depth.Should().Be(0);
        info.PointGeometry.ShouldNotBeNull();
        info.PolygonGeometry.ShouldNotBeNull();

    }

    [Test]
    public void List_Nonexistant_Exception()
    {
        Action act = () => DdbWrapper.List("invalid", "");
        Should.Throw<DdbException>(act);

        act = () => DdbWrapper.List("invalid", "wefrfwef");
        Should.Throw<DdbException>(act);

        act = () => DdbWrapper.List(null, "wefrfwef");
        Should.Throw<DdbException>(act);

/*        act = () => DdbWrapper.List("invalid", (string)null);
        act.Should().Throw<DdbException>();
*/
    }

    [Test]
    public void List_ExistingFileSubFolder_Ok()
    {
        using var fs = new TestFS(Test1ArchiveUrl, nameof(NativeDdbWrapperTests));

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

        var res = DdbWrapper.List(ddbPath, Path.Combine(ddbPath, fileName));

        res.Count.ShouldBe(1);

        var file = res.First();

        file.Path.ShouldBe(fileName);
        // TODO: Handle different timezones
        file.ModifiedTime.ShouldBeInRange(expectedModifiedTime.AddHours(-6), expectedModifiedTime.AddHours(6));
        file.Hash.ShouldBe(expectedHash);
        file.Depth.ShouldBe(expectedDepth);
        file.Size.ShouldBe(expectedSize);
        file.Type.ShouldBe(expectedType);
        file.Properties.ShouldBeEquivalentTo(expectedMeta);
        file.PointGeometry.ShouldNotBeNull();
        //file.PointGeometry.Coordinates.Latitude.Should().BeApproximately(expectedLatitude, 0.00001);
        //file.PointGeometry.Coordinates.Longitude.Should().BeApproximately(expectedLongitude, 0.00001);
        //file.PointGeometry.Coordinates.Altitude.Should().Be(expectedAltitude);

    }

    [Test]
    public void List_ExistingFile_Ok()
    {
        using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

        var ddbPath = Path.Combine(test.TestFolder, "public", "default");

        var res = DdbWrapper.List(ddbPath, Path.Combine(ddbPath, "DJI_0027.JPG"));

        res.Count.ShouldBe(1);
        var entry = res.First();

        Entry expectedEntry = JsonConvert.DeserializeObject<Entry>(
            "{\"depth\":0,\"hash\":\"3157958dd4f2562c8681867dfd6ee5bf70b6e9595b3e3b4b76bbda28342569ed\",\"properties\":{\"cameraPitch\":-89.9000015258789,\"cameraRoll\":0.0,\"cameraYaw\":-131.3000030517578,\"captureTime\":1466699584000.0,\"focalLength\":3.4222222222222225,\"focalLength35\":20.0,\"height\":2250,\"make\":\"DJI\",\"model\":\"FC300S\",\"orientation\":1,\"sensor\":\"dji fc300s\",\"sensorHeight\":3.4650000000000003,\"sensorWidth\":6.16,\"width\":4000},\"mtime\":1491156087,\"path\":\"DJI_0027.JPG\",\"point_geom\":{\"crs\":{\"properties\":{\"name\":\"EPSG:4326\"},\"type\":\"name\"},\"geometry\":{\"coordinates\":[-91.99408299999999,46.84260499999999,198.5099999999999],\"type\":\"Point\"},\"properties\":{},\"type\":\"Feature\"},\"polygon_geom\":{\"crs\":{\"properties\":{\"name\":\"EPSG:4326\"},\"type\":\"name\"},\"geometry\":{\"coordinates\":[[[-91.99397836402999,46.8422402913,158.5099999999999],[-91.99357489543,46.84247729175999,158.5099999999999],[-91.99418894036,46.84296945989999,158.5099999999999],[-91.99459241001999,46.8427324573,158.5099999999999],[-91.99397836402999,46.8422402913,158.5099999999999]]],\"type\":\"Polygon\"},\"properties\":{},\"type\":\"Feature\"},\"size\":3185449,\"type\":3}");

        entry.ShouldBeEquivalentTo(expectedEntry);

    }

    [Test]
    public void List_AllFiles_Ok()
    {
        using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

        var ddbPath = Path.Combine(test.TestFolder, "public", "default");

        var res = DdbWrapper.List(ddbPath, Path.Combine(ddbPath, "."), true);

        res.Count.ShouldBe(26);

        res = DdbWrapper.List(ddbPath, ddbPath, true);

        res.Count.ShouldBe(26);

    }


    [Test]
    public void Remove_Nonexistant_Exception()
    {
        var act = () => DdbWrapper.Remove("invalid", "");
        Should.Throw<DdbException>(act);

        act = () => DdbWrapper.Remove("invalid", "wefrfwef");
        Should.Throw<DdbException>(act);

        act = () => DdbWrapper.Remove(null, "wefrfwef");
        Should.Throw<DdbException>(act);

    }


    [Test]
    public void Remove_ExistingFile_Ok()
    {
        using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

        const string fileName = "DJI_0027.JPG";

        var ddbPath = Path.Combine(test.TestFolder, "public", "default");

        var res = DdbWrapper.List(ddbPath, Path.Combine(ddbPath, fileName));
        res.Count.ShouldBe(1);

        DdbWrapper.Remove(ddbPath, Path.Combine(ddbPath, fileName));

        res = DdbWrapper.List(ddbPath, Path.Combine(ddbPath, fileName));
        res.Count.ShouldBe(0);

    }

    [Test]
    public void Remove_AllFiles_Ok()
    {
        using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

        const string fileName = ".";

        var ddbPath = Path.Combine(test.TestFolder, "public", "default");

        DdbWrapper.Remove(ddbPath, Path.Combine(ddbPath, fileName));

        var res = DdbWrapper.List(ddbPath, ".", true);
        res.Count.ShouldBe(0);

    }

    [Test]
    public void Remove_NonexistantFile_Exception()
    {
        using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

        const string fileName = "elaiuyhrfboeawuyirgfb";

        var ddbPath = Path.Combine(test.TestFolder, "public", "default");

        var act = () => DdbWrapper.Remove(ddbPath, Path.Combine(ddbPath, fileName));

        Should.Throw<DdbException>(act);
    }

    [Test]
    public void Entry_Deserialization_Ok()
    {
        var json = "{'hash': 'abc', 'mtime': 5}";
        var e = JsonConvert.DeserializeObject<Entry>(json);
        e.ModifiedTime.Year.ShouldBe(1970);
    }



    [Test]
    public void Password_HappyPath_Ok()
    {

        using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

        var ddbPath = Path.Combine(test.TestFolder, "public", "default");

        DdbWrapper.VerifyPassword(ddbPath, string.Empty).ShouldBeTrue();

        DdbWrapper.AppendPassword(ddbPath, "testpassword");

        DdbWrapper.VerifyPassword(ddbPath, "testpassword").ShouldBeTrue();
        DdbWrapper.VerifyPassword(ddbPath, "wrongpassword").ShouldBeFalse();

        DdbWrapper.ClearPasswords(ddbPath);
        DdbWrapper.VerifyPassword(ddbPath, "testpassword").ShouldBeFalse();


    }

    [Test]
    public void Chaddr_NullAttr_Exception()
    {

        using var test = new TestFS(Test3ArchiveUrl, BaseTestFolder);

        var ddbPath = test.TestFolder;

        Action act = () => DdbWrapper.ChangeAttributes(ddbPath, null);

        Should.Throw<ArgumentException>(act);

    }

    [Test]
    public void GenerateThumbnail_HappyPath_Ok()
    {

        using var tempFile = new TempFile(TestFileUrl, BaseTestFolder);

        var destPath = Path.Combine(Path.GetTempPath(), "test.jpg");//Path.GetTempFileName();

        try
        {
            DdbWrapper.GenerateThumbnail(tempFile.FilePath, 300, destPath);

            var info = new FileInfo(destPath);
            info.Exists.ShouldBeTrue();
            info.Length.ShouldBeGreaterThan(0);

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
        var buffer = DdbWrapper.GenerateThumbnail(tempFile.FilePath, 300);
        buffer.Length.ShouldBeGreaterThan(0);
    }

    [Test]
    public void GenerateTile_HappyPath_Ok()
    {

        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);

        var destPath = Path.Combine(Path.GetTempPath(), "test.jpg");

        try
        {
            var path = DdbWrapper.GenerateTile(tempFile.FilePath, 18, 64083, 92370, 256, true);
            Debug.WriteLine(path);
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

        var buffer = DdbWrapper.GenerateMemoryTile(tempFile.FilePath, 18, 64083, 92370, 256, true);
        buffer.Length.ShouldBeGreaterThan(0);
    }

    [Test]
    public void Tag_HappyPath_Ok()
    {

        const string goodTag = "pippo/pluto";
        const string goodTagWithRegistry = "https://test.com/pippo/pluto";

        using var test = new TestFS(Test3ArchiveUrl, BaseTestFolder);

        var ddbPath = Path.Combine(test.TestFolder, DdbFolder);

        var tag = DdbWrapper.GetTag(ddbPath);

        tag.ShouldBeNull();

        DdbWrapper.SetTag(ddbPath, goodTag);

        tag = DdbWrapper.GetTag(ddbPath);

        tag.ShouldBe(goodTag);

        DdbWrapper.SetTag(ddbPath, goodTagWithRegistry);

        tag = DdbWrapper.GetTag(ddbPath);

        tag.ShouldBe(goodTagWithRegistry);

    }

    [Test]
    public void Tag_ErrorCases_Ok()
    {

        const string badTag = "pippo";
        const string badTag2 = "����+���+�AAadff_-.-.,";

        using var test = new TestFS(Test3ArchiveUrl, BaseTestFolder);

        var ddbPath = Path.Combine(test.TestFolder, DdbFolder);

        var act = () => DdbWrapper.SetTag(ddbPath, badTag);

        Should.Throw<DdbException>(act);

        act = () => DdbWrapper.SetTag(ddbPath, badTag2);

        Should.Throw<DdbException>(act);

        act = () => DdbWrapper.SetTag(ddbPath, string.Empty);

        Should.Throw<DdbException>(act);

        act = () => DdbWrapper.SetTag(ddbPath, null);

        Should.Throw<ArgumentException>(act);

    }

    [Test]
    public void Stamp_HappyPath_Ok()
    {
        using var test = new TestFS(Test3ArchiveUrl, BaseTestFolder);

        var ddbPath = Path.Combine(test.TestFolder, DdbFolder);

        var stamp = DdbWrapper.GetStamp(ddbPath);
        stamp.Checksum.ShouldNotBeNull();
        stamp.Entries.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public void Delta_HappyPath_Ok()
    {
        using var source = new TestFS(TestDelta2ArchiveUrl, BaseTestFolder);
        using var destination = new TestFS(TestDelta1ArchiveUrl, BaseTestFolder);

        var delta = DdbWrapper.Delta(source.TestFolder, destination.TestFolder);

        delta.Adds.Length.ShouldBeGreaterThan(0);
        delta.Removes.Length.ShouldBeGreaterThan(0);

    }

    [Test]
    public void MoveEntry_SimpleRename_Ok()
    {
        using var test = new TestFS(TestDelta2ArchiveUrl, BaseTestFolder);

        DdbWrapper.MoveEntry(test.TestFolder, "plutone.txt", "test.txt");

        var res = DdbWrapper.List(test.TestFolder, test.TestFolder, true);

        res.Count.ShouldBe(11);
        res[8].Path.ShouldBe("test.txt");
    }

    [Test]
    public void Build_SimpleBuild_Ok()
    {

        using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

        var ddbPath = Path.Combine(test.TestFolder, "public", "default");

        using var tempFile = new TempFile(TestPointCloudUrl, BaseTestFolder);

        var destPath = Path.Combine(ddbPath, Path.GetFileName(tempFile.FilePath));

        File.Move(tempFile.FilePath, destPath);

        var res = DdbWrapper.Add(ddbPath, destPath);

        res.Count.ShouldBe(1);

        DdbWrapper.Build(ddbPath);

    }

    [Test]
    public void IsBuildable_PointCloud_True()
    {

        using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

        var ddbPath = Path.Combine(test.TestFolder, "public", "default");

        using var tempFile = new TempFile(TestPointCloudUrl, BaseTestFolder);

        var destPath = Path.Combine(ddbPath, Path.GetFileName(tempFile.FilePath));

        File.Move(tempFile.FilePath, destPath);

        var res = DdbWrapper.Add(ddbPath, destPath);

        res.Count.ShouldBe(1);

        DdbWrapper.IsBuildable(ddbPath, Path.GetFileName(destPath)).ShouldBeTrue();

    }

    [Test]
    public void IsBuildable_TextFile_False()
    {

        using var test = new TestFS(TestDelta2ArchiveUrl, BaseTestFolder);

        DdbWrapper.IsBuildable(test.TestFolder, "lol.txt").ShouldBeFalse();

    }

    [Test]
    public void IsBuildActive_PointCloud_ConsistentBehavior()
    {
        using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

        var ddbPath = Path.Combine(test.TestFolder, "public", "default");

        using var tempFile = new TempFile(TestPointCloudUrl, BaseTestFolder);

        var destPath = Path.Combine(ddbPath, Path.GetFileName(tempFile.FilePath));

        File.Move(tempFile.FilePath, destPath);

        var res = DdbWrapper.Add(ddbPath, destPath);

        res.Count.ShouldBe(1);

        // Test that IsBuildActive is consistent for buildable files
        var isActive = DdbWrapper.IsBuildActive(ddbPath, Path.GetFileName(destPath));

        // For buildable files, the API should work without exceptions
        // The exact result depends on whether there are pending builds or not
        TestContext.WriteLine($"IsBuildActive for point cloud file returns: {isActive}");

        // Call it multiple times to ensure consistency
        DdbWrapper.IsBuildActive(ddbPath, Path.GetFileName(destPath)).ShouldBe(isActive);
        DdbWrapper.IsBuildActive(ddbPath, Path.GetFileName(destPath)).ShouldBe(isActive);
    }

    [Test]
    public void IsBuildActive_TextFile_False()
    {
        using var test = new TestFS(TestDelta2ArchiveUrl, BaseTestFolder);

        // Text files are not buildable, so build should never be active
        DdbWrapper.IsBuildActive(test.TestFolder, "lol.txt").ShouldBeFalse();
    }

    [Test]
    public void IsBuildActive_PointCloudBuilding_True()
    {
        using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

        var ddbPath = Path.Combine(test.TestFolder, "public", "default");

        using var tempFile = new TempFile(TestPointCloud2Url, BaseTestFolder);

        var destPath = Path.Combine(ddbPath, Path.GetFileName(tempFile.FilePath));

        File.Move(tempFile.FilePath, destPath);

        var res = DdbWrapper.Add(ddbPath, destPath);

        res.Count.ShouldBe(1);

        DdbWrapper.IsBuildActive(ddbPath, Path.GetFileName(destPath)).ShouldBeFalse();

        var isActive = false;

        var task = Task.Run(() =>
        {
            isActive = true;
            TestContext.WriteLine($"Build started");
            DdbWrapper.Build(ddbPath);
            TestContext.WriteLine($"Build finished");
            isActive = false;
        });

        // Wait until the build starts
        while(!isActive) Thread.Sleep(10);

        // While the build is in progress, IsBuildActive should return true
        DdbWrapper.IsBuildActive(ddbPath, Path.GetFileName(destPath)).ShouldBeTrue();

        task.Wait();

        isActive.ShouldBeFalse();

        DdbWrapper.IsBuildActive(ddbPath, Path.GetFileName(destPath)).ShouldBeFalse();

    }

    [Test]
    public void IsBuildActive_NonBuildableFile_ExpectedBehavior()
    {
        using var test = new TestFS(TestDelta2ArchiveUrl, BaseTestFolder);

        // Test with a non-buildable file (text file)
        var textFile = "lol.txt";

        // Verify it's not buildable
        DdbWrapper.IsBuildable(test.TestFolder, textFile).ShouldBeFalse();

        // Test IsBuildActive on non-buildable file
        var isActive = DdbWrapper.IsBuildActive(test.TestFolder, textFile);

        // For non-buildable files, build should never be active
        isActive.ShouldBeFalse();

        TestContext.WriteLine($"IsBuildActive for non-buildable file returns: {isActive}");
    }

    [Test]
    public void MetaAdd_Ok()
    {
        using var area = new TestArea(nameof(MetaAdd_Ok));
        DdbWrapper.Init(area.TestFolder);

        var act = () => DdbWrapper.MetaAdd(area.TestFolder, "test", "123");
        Should.Throw<DdbException>(act); // Needs plural key
        // DdbWrapper.MetaAdd("metaAddTest", "", "tests", "123").Data.ToObject<int>().Should().Be(123);
    }

    [Test]
    public void MetaAdd_Json()
    {
        using var area = new TestArea(nameof(MetaAdd_Json));
        DdbWrapper.Init(area.TestFolder);

        var res = DdbWrapper.MetaAdd(area.TestFolder, "tests", "{\"test\": true}");
        JsonConvert.SerializeObject(res.Data).ShouldBe("{\"test\":true}");
        res.Id.ShouldNotBeNull();
        res.ModifiedTime.ShouldBeInRange(DateTime.UtcNow.AddSeconds(-3), DateTime.UtcNow.AddSeconds(3));
    }

    [Test]
    public void MetaSet_Ok()
    {
        using var area = new TestArea(nameof(MetaSet_Ok));
        DdbWrapper.Init(area.TestFolder);

        var f = Path.Join(area.TestFolder, "test.txt");
        File.WriteAllText(f, null);

        DdbWrapper.Add(area.TestFolder, f);

        var act = () => DdbWrapper.MetaSet(area.TestFolder, "tests", "123", f);
        Should.Throw<DdbException>(act); // Needs singular key

        DdbWrapper.MetaSet(area.TestFolder, "test", "abc", f).Data.ToObject<string>().ShouldBe("abc");
        DdbWrapper.MetaSet(area.TestFolder, "test", "efg", f).Data.ToObject<string>().ShouldBe("efg");
    }

    [Test]
    public void MetaRemove_Ok()
    {
        using var area = new TestArea(nameof(MetaRemove_Ok));
        DdbWrapper.Init(area.TestFolder);

        var id = DdbWrapper.MetaSet(area.TestFolder, "test", "123").Id;
        DdbWrapper.MetaRemove(area.TestFolder, "invalid").ShouldBe(0);
        DdbWrapper.MetaRemove(area.TestFolder, id).ShouldBe(1);
        DdbWrapper.MetaRemove(area.TestFolder, id).ShouldBe(0);
    }

    [Test]
    public void MetaGet_Ok()
    {
        using var area = new TestArea(nameof(MetaGet_Ok));
        DdbWrapper.Init(area.TestFolder);

        DdbWrapper.MetaSet(area.TestFolder, "abc", "true");

        var act1 = () => DdbWrapper.MetaGet(area.TestFolder, "nonexistant");
        Should.Throw<DdbException>(act1);

        var act2 = () => DdbWrapper.MetaGet(area.TestFolder, "abc", "123");
        Should.Throw<DdbException>(act2);

        JsonConvert.DeserializeObject<Meta>(DdbWrapper.MetaGet(area.TestFolder, "abc")).Data.ToObject<bool>()
            .ShouldBeTrue();
    }

    [Test]
    public void MetaGet_Ok2()
    {
        using var area = new TestArea(nameof(MetaGet_Ok2));
        DdbWrapper.Init(area.TestFolder);

        DdbWrapper.MetaAdd(area.TestFolder, "tests", "{\"test\":true}");
        DdbWrapper.MetaAdd(area.TestFolder, "tests", "{\"test\":false}");
        DdbWrapper.MetaAdd(area.TestFolder, "tests", "{\"test\":null}");

        var res = JsonConvert.DeserializeObject<Meta[]>(DdbWrapper.MetaGet(area.TestFolder, "tests"));

        res.Length.ShouldBe(3);

    }

    [Test]
    public void MetaUnset_Ok()
    {
        using var area = new TestArea(nameof(MetaUnset_Ok));
        DdbWrapper.Init(area.TestFolder);

        var f = Path.Join(area.TestFolder, "test.txt");
        File.WriteAllText(f, null);

        DdbWrapper.Add(area.TestFolder, f);

        DdbWrapper.MetaSet(area.TestFolder, "abc", "[1,2,3]");
        DdbWrapper.MetaUnset(area.TestFolder, "abc", f).ShouldBe(0);
        DdbWrapper.MetaUnset(area.TestFolder, "abc").ShouldBe(1);
        DdbWrapper.MetaUnset(area.TestFolder, "abc").ShouldBe(0);
    }

    [Test]
    public void MetaList_Ok()
    {
        using var area = new TestArea(nameof(MetaList_Ok));
        DdbWrapper.Init(area.TestFolder);

        DdbWrapper.MetaAdd(area.TestFolder, "annotations", "123");
        DdbWrapper.MetaAdd(area.TestFolder, "examples", "abc");
        DdbWrapper.MetaList(area.TestFolder).Count.ShouldBe(2);
    }

    [Test]
    public void Stac_Ok()
    {

        using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

        var ddbPath = Path.Combine(test.TestFolder, "public", "default");

        var res = DdbWrapper.Stac(ddbPath, "DJI_0025.JPG",
            "http://localhost:5000/orgs/public/ds/default", "public/default", "http://localhost:5000");

        res.ShouldNotBeNull();

        TestContext.WriteLine(res);
    }

    [Test]
    public void Stac_NullPath_Ok()
    {

        using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

        var ddbPath = Path.Combine(test.TestFolder, "public", "default");

        var res = DdbWrapper.Stac(ddbPath, null,
            "http://localhost:5000/orgs/public/ds/default", "public/default", "http://localhost:5000");

        res.ShouldNotBeNull();

        TestContext.WriteLine(res);

    }

    [Test]
    public void RescanIndex_ExistingDatabase_Ok()
    {
        using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

        var ddbPath = Path.Combine(test.TestFolder, "public", "default");

        // Use stopOnError = false because test dataset may not have all physical files
        var res = DdbWrapper.RescanIndex(ddbPath, null, false);

        res.ShouldNotBeNull();
        res.ShouldNotBeEmpty();

        foreach (var entry in res)
        {
            TestContext.WriteLine($"Path: {entry.Path}, Success: {entry.Success}, Hash: {entry.Hash}, Error: {entry.Error}");
            entry.Path.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Test]
    public void RescanIndex_WithTypesFilter_Ok()
    {
        using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

        var ddbPath = Path.Combine(test.TestFolder, "public", "default");

        // Rescan only images with stopOnError = false for test dataset
        var res = DdbWrapper.RescanIndex(ddbPath, "image,geoimage", false);

        res.ShouldNotBeNull();

        foreach (var entry in res)
        {
            TestContext.WriteLine($"Path: {entry.Path}, Success: {entry.Success}, Hash: {entry.Hash}");
        }
    }

    [Test]
    public void RescanIndex_NonexistentPath_Exception()
    {
        Action act = () => DdbWrapper.RescanIndex("nonexistent");
        Should.Throw<DdbException>(act);
    }

    [Test]
    public void RescanIndex_StopOnErrorFalse_ContinuesOnError()
    {
        using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

        var ddbPath = Path.Combine(test.TestFolder, "public", "default");

        // Should not throw even if there are errors, when stopOnError is false
        var res = DdbWrapper.RescanIndex(ddbPath, null, false);

        res.ShouldNotBeNull();
    }

    #region UTF-8 Path Tests

    [Test]
    public void Init_Utf8EuropeanAccents_Ok()
    {
        using var area = new TestArea("UTF8_àèìòù_ñüöß");
        DdbWrapper.Init(area.TestFolder).ShouldContain(area.TestFolder);
        Directory.Exists(Path.Join(area.TestFolder, DdbFolder)).ShouldBeTrue();
    }

    [Test]
    public void Init_Utf8CJK_Ok()
    {
        using var area = new TestArea("UTF8_测试数据_テスト");
        DdbWrapper.Init(area.TestFolder).ShouldContain(area.TestFolder);
        Directory.Exists(Path.Join(area.TestFolder, DdbFolder)).ShouldBeTrue();
    }

    [Test]
    public void Init_Utf8Arabic_Ok()
    {
        using var area = new TestArea("UTF8_بيانات_طائرة");
        DdbWrapper.Init(area.TestFolder).ShouldContain(area.TestFolder);
        Directory.Exists(Path.Join(area.TestFolder, DdbFolder)).ShouldBeTrue();
    }

    [Test]
    public void Init_Utf8Emoji_Ok()
    {
        using var area = new TestArea("UTF8_📸drone_🌍geo");
        DdbWrapper.Init(area.TestFolder).ShouldContain(area.TestFolder);
        Directory.Exists(Path.Join(area.TestFolder, DdbFolder)).ShouldBeTrue();
    }

    [Test]
    public void EndToEnd_Utf8EuropeanAccents_AddListRemove()
    {
        using var area = new TestArea("UTF8_città_données");
        DdbWrapper.Init(area.TestFolder);

        var fileName = "foto_città.txt";
        var filePath = Path.Join(area.TestFolder, fileName);
        File.WriteAllText(filePath, "contenuto test àèìòù");

        // Add
        var addResult = DdbWrapper.Add(area.TestFolder, filePath);
        addResult.Count.ShouldBe(1);
        addResult[0].Path.ShouldBe(fileName);

        // List
        var listResult = DdbWrapper.List(area.TestFolder, Path.Combine(area.TestFolder, "."), true);
        listResult.Count.ShouldBe(1);
        listResult[0].Path.ShouldBe(fileName);

        // Info
        var infoResult = DdbWrapper.Info(filePath);
        infoResult.Count.ShouldBe(1);

        // Remove
        DdbWrapper.Remove(area.TestFolder, filePath);
        var afterRemove = DdbWrapper.List(area.TestFolder, Path.Combine(area.TestFolder, "."), true);
        afterRemove.Count.ShouldBe(0);
    }

    [Test]
    public void EndToEnd_Utf8CJK_AddListRemove()
    {
        using var area = new TestArea("UTF8_飞行数据_CJK");
        DdbWrapper.Init(area.TestFolder);

        var fileName = "测试文件.txt";
        var filePath = Path.Join(area.TestFolder, fileName);
        File.WriteAllText(filePath, "中文内容测试");

        // Add
        var addResult = DdbWrapper.Add(area.TestFolder, filePath);
        addResult.Count.ShouldBe(1);
        addResult[0].Path.ShouldBe(fileName);

        // List
        var listResult = DdbWrapper.List(area.TestFolder, Path.Combine(area.TestFolder, "."), true);
        listResult.Count.ShouldBe(1);
        listResult[0].Path.ShouldBe(fileName);

        // Info
        var infoResult = DdbWrapper.Info(filePath);
        infoResult.Count.ShouldBe(1);

        // Remove
        DdbWrapper.Remove(area.TestFolder, filePath);
        var afterRemove = DdbWrapper.List(area.TestFolder, Path.Combine(area.TestFolder, "."), true);
        afterRemove.Count.ShouldBe(0);
    }

    [Test]
    public void EndToEnd_Utf8Arabic_AddListRemove()
    {
        using var area = new TestArea("UTF8_بيانات_Arabic");
        DdbWrapper.Init(area.TestFolder);

        var fileName = "ملف_بيانات.txt";
        var filePath = Path.Join(area.TestFolder, fileName);
        File.WriteAllText(filePath, "محتوى الاختبار");

        // Add
        var addResult = DdbWrapper.Add(area.TestFolder, filePath);
        addResult.Count.ShouldBe(1);
        addResult[0].Path.ShouldBe(fileName);

        // List
        var listResult = DdbWrapper.List(area.TestFolder, Path.Combine(area.TestFolder, "."), true);
        listResult.Count.ShouldBe(1);
        listResult[0].Path.ShouldBe(fileName);

        // Info
        var infoResult = DdbWrapper.Info(filePath);
        infoResult.Count.ShouldBe(1);

        // Remove
        DdbWrapper.Remove(area.TestFolder, filePath);
        var afterRemove = DdbWrapper.List(area.TestFolder, Path.Combine(area.TestFolder, "."), true);
        afterRemove.Count.ShouldBe(0);
    }

    [Test]
    public void EndToEnd_Utf8Emoji_AddListRemove()
    {
        using var area = new TestArea("UTF8_📸_Emoji");
        DdbWrapper.Init(area.TestFolder);

        var fileName = "📸drone_photo.txt";
        var filePath = Path.Join(area.TestFolder, fileName);
        File.WriteAllText(filePath, "emoji test content 🌍");

        // Add
        var addResult = DdbWrapper.Add(area.TestFolder, filePath);
        addResult.Count.ShouldBe(1);
        addResult[0].Path.ShouldBe(fileName);

        // List
        var listResult = DdbWrapper.List(area.TestFolder, Path.Combine(area.TestFolder, "."), true);
        listResult.Count.ShouldBe(1);
        listResult[0].Path.ShouldBe(fileName);

        // Info
        var infoResult = DdbWrapper.Info(filePath);
        infoResult.Count.ShouldBe(1);

        // Remove
        DdbWrapper.Remove(area.TestFolder, filePath);
        var afterRemove = DdbWrapper.List(area.TestFolder, Path.Combine(area.TestFolder, "."), true);
        afterRemove.Count.ShouldBe(0);
    }

    [Test]
    public void EndToEnd_Utf8MixedPath_AddListRemove()
    {
        using var area = new TestArea("UTF8_Mixed_αβγ_日本語");
        DdbWrapper.Init(area.TestFolder);

        // Create a subdirectory with mixed Unicode characters
        var subDir = Path.Join(area.TestFolder, "données_飞行");
        Directory.CreateDirectory(subDir);

        var fileName = "données_飞行/image_città_テスト.txt";
        var filePath = Path.Join(area.TestFolder, fileName);
        File.WriteAllText(filePath, "mixed unicode content");

        // Add - DDB indexes both the directory and the file
        var addResult = DdbWrapper.Add(area.TestFolder, filePath);
        addResult.Count.ShouldBeGreaterThanOrEqualTo(1);
        addResult.ShouldContain(e => e.Path == fileName.Replace('\\', '/'));

        // List
        var listResult = DdbWrapper.List(area.TestFolder, Path.Combine(area.TestFolder, "."), true);
        listResult.Count.ShouldBeGreaterThanOrEqualTo(1);
        listResult.ShouldContain(e => e.Path == fileName.Replace('\\', '/'));

        // Info
        var infoResult = DdbWrapper.Info(filePath);
        infoResult.Count.ShouldBe(1);

        // Remove
        DdbWrapper.Remove(area.TestFolder, filePath);
        var afterRemove = DdbWrapper.List(area.TestFolder, Path.Combine(area.TestFolder, "."), true);
        afterRemove.Count.ShouldBe(0);
    }

    [Test]
    public void Add_Utf8MultipleFiles_Ok()
    {
        using var area = new TestArea("UTF8_Batch_données");
        DdbWrapper.Init(area.TestFolder);

        var file1 = Path.Join(area.TestFolder, "café.txt");
        var file2 = Path.Join(area.TestFolder, "naïve.txt");
        var file3 = Path.Join(area.TestFolder, "résumé.txt");
        File.WriteAllText(file1, "test1");
        File.WriteAllText(file2, "test2");
        File.WriteAllText(file3, "test3");

        // Add multiple files at once
        var addResult = DdbWrapper.Add(area.TestFolder, [file1, file2, file3]);
        addResult.Count.ShouldBe(3);

        var paths = addResult.Select(e => e.Path).ToArray();
        paths.ShouldContain("café.txt");
        paths.ShouldContain("naïve.txt");
        paths.ShouldContain("résumé.txt");

        // Verify list returns same paths
        var listResult = DdbWrapper.List(area.TestFolder, Path.Combine(area.TestFolder, "."), true);
        listResult.Count.ShouldBe(3);

        var listPaths = listResult.Select(e => e.Path).ToArray();
        listPaths.ShouldContain("café.txt");
        listPaths.ShouldContain("naïve.txt");
        listPaths.ShouldContain("résumé.txt");
    }

    [Test]
    public void Remove_Utf8MultipleFiles_Ok()
    {
        using var area = new TestArea("UTF8_BatchRemove");
        DdbWrapper.Init(area.TestFolder);

        var file1 = Path.Join(area.TestFolder, "données_1.txt");
        var file2 = Path.Join(area.TestFolder, "données_2.txt");
        File.WriteAllText(file1, "test1");
        File.WriteAllText(file2, "test2");

        DdbWrapper.Add(area.TestFolder, [file1, file2]);

        // Remove multiple UTF-8 files at once
        DdbWrapper.Remove(area.TestFolder, [file1, file2]);

        var listResult = DdbWrapper.List(area.TestFolder, Path.Combine(area.TestFolder, "."), true);
        listResult.Count.ShouldBe(0);
    }

    [Test]
    public void Info_Utf8WithHash_Ok()
    {
        using var area = new TestArea("UTF8_Info_Hash");
        var filePath = Path.Join(area.TestFolder, "données_ñ_ü.txt");
        File.WriteAllText(filePath, "hash test content");

        var infoResult = DdbWrapper.Info(filePath, withHash: true);
        infoResult.Count.ShouldBe(1);
        infoResult[0].Hash.ShouldNotBeNullOrWhiteSpace();
        infoResult[0].Size.ShouldBeGreaterThan(0);
    }

    #endregion

    #region Raster Analysis

    // Helper: parses a JSON response and returns the parsed JObject, ensuring
    // the required top-level fields that are shared by GetRasterValueInfo and
    // GetRasterPointValue responses are present.
    private static JObject ParseAndValidateRasterJson(string json, params string[] requiredFields)
    {
        json.ShouldNotBeNullOrWhiteSpace();
        var obj = JObject.Parse(json);
        foreach (var field in requiredFields)
            obj[field].ShouldNotBeNull($"Field '{field}' missing in raster JSON response");
        return obj;
    }

    [Test]
    public void GetRasterValueInfo_HappyPath_Ok()
    {
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);

        var json = DdbWrapper.GetRasterValueInfo(tempFile.FilePath);

        var obj = ParseAndValidateRasterJson(
            json,
            "width", "height", "bandCount", "dataType",
            "valueMin", "valueMax", "unit", "isThermal",
            "isDirectValue", "sensorId");

        ((int)obj["width"]!).ShouldBeGreaterThan(0);
        ((int)obj["height"]!).ShouldBeGreaterThan(0);
        ((int)obj["bandCount"]!).ShouldBeGreaterThanOrEqualTo(1);
        ((bool)obj["isThermal"]!).ShouldBeFalse("RGB orthophoto should not be detected as thermal");
        ((float)obj["valueMax"]!).ShouldBeGreaterThanOrEqualTo((float)obj["valueMin"]!);
    }

    [Test]
    public void GetRasterValueInfo_NullPath_Throws()
    {
        Should.Throw<ArgumentException>(() => DdbWrapper.GetRasterValueInfo(null!));
    }

    [Test]
    public void GetRasterValueInfo_NonExistentPath_Throws()
    {
        Should.Throw<DdbException>(() => DdbWrapper.GetRasterValueInfo(
            Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".tif")));
    }

    [Test]
    public void GetRasterPointValue_HappyPath_Ok()
    {
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);

        // Read dimensions first so we can pick a valid interior pixel.
        var info = JObject.Parse(DdbWrapper.GetRasterValueInfo(tempFile.FilePath));
        var w = (int)info["width"]!;
        var h = (int)info["height"]!;
        var px = w / 2;
        var py = h / 2;

        var json = DdbWrapper.GetRasterPointValue(tempFile.FilePath, px, py);

        var obj = ParseAndValidateRasterJson(
            json, "x", "y", "value", "rawValue", "isThermal", "hasGeo");

        ((int)obj["x"]!).ShouldBe(px);
        ((int)obj["y"]!).ShouldBe(py);
    }

    [Test]
    public void GetRasterPointValue_OutOfBounds_Throws()
    {
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);
        Should.Throw<DdbException>(() => DdbWrapper.GetRasterPointValue(tempFile.FilePath, -1, -1));
        Should.Throw<DdbException>(() => DdbWrapper.GetRasterPointValue(tempFile.FilePath, 10_000_000, 10_000_000));
    }

    [Test]
    public void GetRasterPointValue_NullPath_Throws()
    {
        Should.Throw<ArgumentException>(() => DdbWrapper.GetRasterPointValue(null!, 0, 0));
    }

    [Test]
    public void GetRasterAreaStats_HappyPath_Ok()
    {
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);

        var info = JObject.Parse(DdbWrapper.GetRasterValueInfo(tempFile.FilePath));
        var w = (int)info["width"]!;
        var h = (int)info["height"]!;
        // Sample a modest interior rectangle to keep the test fast.
        var x0 = w / 4;
        var y0 = h / 4;
        var x1 = x0 + Math.Min(32, w / 2);
        var y1 = y0 + Math.Min(32, h / 2);

        var json = DdbWrapper.GetRasterAreaStats(tempFile.FilePath, x0, y0, x1, y1);

        var obj = ParseAndValidateRasterJson(
            json, "min", "max", "mean", "stddev", "median", "pixelCount", "bounds", "unit", "isThermal");

        ((int)obj["pixelCount"]!).ShouldBeGreaterThan(0);
        ((float)obj["max"]!).ShouldBeGreaterThanOrEqualTo((float)obj["min"]!);
    }

    [Test]
    public void GetRasterAreaStats_SwappedCorners_Ok()
    {
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);

        var info = JObject.Parse(DdbWrapper.GetRasterValueInfo(tempFile.FilePath));
        var w = (int)info["width"]!;
        var h = (int)info["height"]!;

        // Upper-right / lower-left corner order should be normalized by the native code.
        var json = DdbWrapper.GetRasterAreaStats(tempFile.FilePath, w / 2 + 16, h / 2 + 16, w / 2, h / 2);
        var obj = ParseAndValidateRasterJson(json, "min", "max", "mean", "pixelCount");
        ((int)obj["pixelCount"]!).ShouldBeGreaterThan(0);
    }

    [Test]
    public void GetRasterAreaStats_CoordsClampedToRaster_Ok()
    {
        // Native code clamps out-of-range coordinates to the raster extent rather
        // than throwing. Verify this well-defined behavior: fully out-of-range
        // corners get clamped down to a single valid pixel area.
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);

        var json = DdbWrapper.GetRasterAreaStats(tempFile.FilePath, -1000, -1000, -1, -1);
        var obj = ParseAndValidateRasterJson(json, "min", "max", "pixelCount", "bounds");
        ((int)obj["pixelCount"]!).ShouldBeGreaterThan(0);
    }

    [Test]
    public void GetRasterAreaStats_NullPath_Throws()
    {
        Should.Throw<ArgumentException>(() => DdbWrapper.GetRasterAreaStats(null!, 0, 0, 1, 1));
    }

    [Test]
    public void GetRasterProfile_HappyPath_Ok()
    {
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);

        // Build a short horizontal LineString around the dataset's center (Brighton ortho
        // covers ~40.78 N / -74.17 W). Using a tiny delta keeps the test fast while still
        // exercising bilinear sampling along multiple pixels.
        // Values are read from the ortho's first band (red channel).
        var lonStart = -74.1720;
        var lonEnd = -74.1715;
        var lat = 40.7801;
        var lineString = JsonConvert.SerializeObject(new
        {
            type = "LineString",
            coordinates = new[]
            {
                new[] { lonStart, lat },
                new[] { lonEnd, lat }
            }
        });

        var json = DdbWrapper.GetRasterProfile(tempFile.FilePath, lineString, 32);

        var obj = ParseAndValidateRasterJson(json,
            "samples", "totalLength", "sampleCount", "validCount", "unit", "isThermal");

        ((int)obj["sampleCount"]!).ShouldBe(32);
        ((JArray)obj["samples"]!).Count.ShouldBe(32);
        ((double)obj["totalLength"]!).ShouldBeGreaterThan(0.0);
    }

    [Test]
    public void GetRasterProfile_NullPath_Throws()
    {
        Should.Throw<ArgumentException>(() => DdbWrapper.GetRasterProfile(null!,
            "{\"type\":\"LineString\",\"coordinates\":[[0,0],[1,1]]}", 16));
    }

    [Test]
    public void GetRasterProfile_NullGeometry_Throws()
    {
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);
        Should.Throw<ArgumentException>(() => DdbWrapper.GetRasterProfile(tempFile.FilePath, null!, 16));
    }

    [Test]
    public void GetRasterProfile_InvalidGeoJson_Throws()
    {
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);
        Should.Throw<DdbException>(() => DdbWrapper.GetRasterProfile(tempFile.FilePath, "{not-json}", 16));
    }

    [Test]
    public void GetRasterProfile_WrongGeometryType_Throws()
    {
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);
        var point = "{\"type\":\"Point\",\"coordinates\":[-74.17,40.78]}";
        Should.Throw<DdbException>(() => DdbWrapper.GetRasterProfile(tempFile.FilePath, point, 16));
    }

    [Test]
    public void GetRasterProfile_SamplesClamped_Ok()
    {
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);
        var lineString =
            "{\"type\":\"LineString\",\"coordinates\":[[-74.1720,40.7801],[-74.1715,40.7801]]}";

        // Very large sample count is clamped to 4096.
        var big = JObject.Parse(DdbWrapper.GetRasterProfile(tempFile.FilePath, lineString, 100_000));
        ((int)big["sampleCount"]!).ShouldBeLessThanOrEqualTo(4096);

        // Sub-minimum sample counts fall back to a sane default >= 2.
        var small = JObject.Parse(DdbWrapper.GetRasterProfile(tempFile.FilePath, lineString, 0));
        ((int)small["sampleCount"]!).ShouldBeGreaterThanOrEqualTo(2);
    }

    #endregion

    #region CalculateVolume / DetectStockpile

    [Test]
    public void CalculateVolume_HappyPath_Ok()
    {
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);

        // Tiny polygon well inside the Brighton ortho (UTM 15N, WGS84 bounds
        // lon [-91.99442, -91.99323], lat [46.84218, 46.84298]).
        var polygon = "{\"type\":\"Polygon\",\"coordinates\":[[" +
            "[-91.99400,46.84240]," +
            "[-91.99360,46.84240]," +
            "[-91.99360,46.84270]," +
            "[-91.99400,46.84270]," +
            "[-91.99400,46.84240]]]}";

        var json = DdbWrapper.CalculateVolume(tempFile.FilePath, polygon, "lowest_perimeter", 0.0);
        var obj = JObject.Parse(json);
        obj.ShouldNotBeNull();
        obj["cutVolume"].ShouldNotBeNull();
        obj["fillVolume"].ShouldNotBeNull();
        obj["netVolume"].ShouldNotBeNull();
        obj["area2d"].ShouldNotBeNull();
        ((double)obj["area2d"]!).ShouldBeGreaterThan(0.0);
        ((string)obj["basePlaneMethod"]!).ShouldBe("lowest_perimeter");
    }

    [Test]
    public void CalculateVolume_NullPath_Throws()
    {
        Should.Throw<ArgumentException>(() => DdbWrapper.CalculateVolume(null!,
            "{\"type\":\"Polygon\",\"coordinates\":[[[0,0],[1,0],[1,1],[0,0]]]}", "lowest_perimeter", 0.0));
    }

    [Test]
    public void CalculateVolume_InvalidMethod_Throws()
    {
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);
        var polygon = "{\"type\":\"Polygon\",\"coordinates\":[[" +
            "[-91.99400,46.84240],[-91.99360,46.84240],[-91.99360,46.84270]," +
            "[-91.99400,46.84270],[-91.99400,46.84240]]]}";
        Should.Throw<DdbException>(() => DdbWrapper.CalculateVolume(tempFile.FilePath, polygon, "not_a_method", 0.0));
    }

    [Test]
    public void DetectStockpile_HappyPath_Ok()
    {
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);
        // Click at the Brighton ortho center.
        var json = DdbWrapper.DetectStockpile(tempFile.FilePath, 46.84258, -91.99383, 15.0, 0.5f);
        var obj = JObject.Parse(json);
        obj["polygon"].ShouldNotBeNull();
        obj["estimatedVolume"].ShouldNotBeNull();
        obj["confidence"].ShouldNotBeNull();
        ((string)obj["polygon"]!["type"]!).ShouldBe("Polygon");
    }

    [Test]
    public void DetectStockpile_NullPath_Throws()
    {
        Should.Throw<ArgumentException>(() => DdbWrapper.DetectStockpile(null!, 0.0, 0.0, 10.0, 0.5f));
    }

    [Test]
    public void DetectStockpile_NonPositiveRadius_Throws()
    {
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);
        Should.Throw<ArgumentException>(() => DdbWrapper.DetectStockpile(tempFile.FilePath, 46.84258, -91.99383, 0.0, 0.5f));
    }

    [Test]
    public void DetectAllStockpiles_HappyPath_Ok()
    {
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);
        var json = DdbWrapper.DetectAllStockpiles(tempFile.FilePath, 0.5f, 0.0, 50);
        var obj = JObject.Parse(json);
        obj["stockpiles"].ShouldNotBeNull();
        obj["totalFound"].ShouldNotBeNull();
        obj["sensitivityUsed"].ShouldNotBeNull();
    }

    [Test]
    public void DetectAllStockpiles_NullPath_Throws()
    {
        Should.Throw<ArgumentException>(() => DdbWrapper.DetectAllStockpiles(null!, 0.5f, 0.0, 50));
    }

    [Test]
    public void DetectAllStockpiles_InvalidSensitivity_Throws()
    {
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);
        Should.Throw<ArgumentException>(() => DdbWrapper.DetectAllStockpiles(tempFile.FilePath, -0.5f, 0.0, 50));
        Should.Throw<ArgumentException>(() => DdbWrapper.DetectAllStockpiles(tempFile.FilePath, 1.5f, 0.0, 50));
    }

    [Test]
    public void DetectAllStockpiles_NegativeMinArea_Throws()
    {
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);
        Should.Throw<ArgumentException>(() => DdbWrapper.DetectAllStockpiles(tempFile.FilePath, 0.5f, -1.0, 50));
    }

    [Test]
    public void DetectAllStockpiles_NonPositiveMaxResults_Throws()
    {
        using var tempFile = new TempFile(TestGeoTiffUrl, BaseTestFolder);
        Should.Throw<ArgumentException>(() => DdbWrapper.DetectAllStockpiles(tempFile.FilePath, 0.5f, 0.0, 0));
    }

    #endregion

    [Test]
    [Explicit("Clean test directory")]
    public void Clean_Domain()
    {
        TempFile.CleanDomain(BaseTestFolder);
        TestFS.ClearCache(BaseTestFolder);
    }
}