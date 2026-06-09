using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Registry.Adapters.Archives;
using Shouldly;

namespace Registry.Adapters.Test;

[TestFixture]
public class SharpCompressArchiveExtractorTest
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ddb_extract_test_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Test]
    public void IsSupported_RecognizesArchiveExtensions()
    {
        var ex = new SharpCompressArchiveExtractor();

        ex.IsSupported("flight.zip").ShouldBeTrue();
        ex.IsSupported("bundle.tar.gz").ShouldBeTrue();
        ex.IsSupported("bundle.tgz").ShouldBeTrue();
        ex.IsSupported("a.7z").ShouldBeTrue();
        ex.IsSupported("a.rar").ShouldBeTrue();
        ex.IsSupported("dem.tif.gz").ShouldBeTrue();

        ex.IsSupported("photo.jpg").ShouldBeFalse();
        ex.IsSupported("notes.txt").ShouldBeFalse();
        ex.IsSupported("").ShouldBeFalse();
        ex.IsSupported(null!).ShouldBeFalse();
    }

    [Test]
    public void Open_Zip_CountsEntriesAndReadsContent()
    {
        var zipPath = Path.Combine(_dir, "test.zip");
        CreateZip(zipPath, new Dictionary<string, string>
        {
            ["a.txt"] = "hello",
            ["sub/b.txt"] = "world!!"
        });

        var ex = new SharpCompressArchiveExtractor();
        using var session = ex.Open(zipPath);

        session.FileEntryCount.ShouldBe(2);
        session.TotalUncompressedBytes.ShouldBe(12); // "hello" (5) + "world!!" (7)

        var entries = session.Entries().Where(e => !e.IsDirectory).ToList();
        entries.Select(e => e.Key).ShouldContain("a.txt");
        entries.Select(e => e.Key).ShouldContain("sub/b.txt");

        var a = entries.First(e => e.Key == "a.txt");
        using var stream = a.OpenStream();
        using var reader = new StreamReader(stream);
        reader.ReadToEnd().ShouldBe("hello");
    }

    [Test]
    public void Open_SingleFileGz_DerivesNameFromArchive()
    {
        var gzPath = Path.Combine(_dir, "data.txt.gz");
        CreateGz(gzPath, "compressed-content");

        var ex = new SharpCompressArchiveExtractor();
        using var session = ex.Open(gzPath);

        session.FileEntryCount.ShouldBe(1);

        var entry = session.Entries().Single(e => !e.IsDirectory);
        entry.Key.ShouldNotBeNullOrEmpty();
        entry.Key.ShouldNotEndWith(".gz");
        entry.Key.ShouldBe("data.txt");

        using var stream = entry.OpenStream();
        using var reader = new StreamReader(stream);
        reader.ReadToEnd().ShouldBe("compressed-content");
    }

    [Test]
    public void Open_TarGz_ExtractsEntries()
    {
        var tgzPath = Path.Combine(_dir, "bundle.tar.gz");
        CreateTarGz(tgzPath, new Dictionary<string, string>
        {
            ["one.txt"] = "1",
            ["dir/two.txt"] = "22"
        });

        var ex = new SharpCompressArchiveExtractor();
        using var session = ex.Open(tgzPath);

        session.FileEntryCount.ShouldBe(2);

        var keys = session.Entries().Where(e => !e.IsDirectory).Select(e => e.Key).ToList();
        keys.ShouldContain("one.txt");
        keys.ShouldContain("dir/two.txt");
    }

    private static void CreateZip(string path, IReadOnlyDictionary<string, string> files)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in files)
        {
            var entry = zip.CreateEntry(name);
            using var s = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            s.Write(bytes, 0, bytes.Length);
        }
    }

    private static void CreateGz(string path, string content)
    {
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        var bytes = Encoding.UTF8.GetBytes(content);
        gz.Write(bytes, 0, bytes.Length);
    }

    private static void CreateTarGz(string path, IReadOnlyDictionary<string, string> files)
    {
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        using var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: false);
        foreach (var (name, content) in files)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(bytes)
            };
            tar.WriteEntry(entry);
        }
    }
}
