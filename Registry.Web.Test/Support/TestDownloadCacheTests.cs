#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Test.Common;
using Shouldly;

namespace Registry.Web.Test.Support;

/// <summary>
/// The test-archive cache behind <see cref="TestFS"/> and <see cref="TempFile"/>: a partial or
/// corrupt download is never reused, and a writer that loses the rename uses the winner's file.
/// </summary>
[TestFixture]
public class TestDownloadCacheTests
{
    private const string Url = "https://example.invalid/registry/Archive.zip";
    private static readonly byte[] Full = RandomNumberGenerator.GetBytes(64 * 1024);

    private string _cacheFolder = null!;
    private string _finalPath = null!;
    private int _downloads;

    [SetUp]
    public void SetUp()
    {
        _cacheFolder = Path.Combine(Path.GetTempPath(), nameof(TestDownloadCacheTests), Guid.NewGuid().ToString("N"));
        _finalPath = Path.Combine(_cacheFolder, "Archive.zip");
        _downloads = 0;
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_cacheFolder))
            Directory.Delete(_cacheFolder, true);
    }

    private Task Download(string url, string target)
    {
        url.ShouldBe(Url);
        _downloads++;
        File.WriteAllBytes(target, Full);
        return Task.CompletedTask;
    }

    private static Task MustNotDownload(string url, string target) =>
        throw new AssertionException("The cached file should have been reused");

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    [Test]
    public void FirstUse_DownloadsAndRecordsHash_ThenReuses()
    {
        TestDownloadCache.GetOrDownload(Url, _cacheFolder, Download).ShouldBe(_finalPath);

        _downloads.ShouldBe(1);
        File.ReadAllBytes(_finalPath).ShouldBe(Full);
        File.ReadAllText(TestDownloadCache.HashPath(_finalPath)).ShouldBe(Sha256(Full));

        TestDownloadCache.GetOrDownload(Url, _cacheFolder, MustNotDownload).ShouldBe(_finalPath);
        Directory.GetFiles(_cacheFolder, "*" + TestDownloadCache.PartialSuffix).ShouldBeEmpty();
    }

    [Test]
    public void InterruptedDownload_LeavesNoCacheEntry_AndNextRunDownloadsAgain()
    {
        Should.Throw<IOException>(() => TestDownloadCache.GetOrDownload(Url, _cacheFolder, (_, target) =>
        {
            File.WriteAllBytes(target, Full.AsSpan(0, 1000).ToArray());
            throw new IOException("connection reset");
        }));

        File.Exists(_finalPath).ShouldBeFalse();
        Directory.GetFiles(_cacheFolder, "*" + TestDownloadCache.PartialSuffix).ShouldBeEmpty();

        TestDownloadCache.GetOrDownload(Url, _cacheFolder, Download);

        _downloads.ShouldBe(1);
        File.ReadAllBytes(_finalPath).ShouldBe(Full);
    }

    [Test]
    public void TruncatedArchive_WithoutHash_IsDownloadedAgain()
    {
        // What the old non-atomic cache left behind when a run was killed mid-download.
        Directory.CreateDirectory(_cacheFolder);
        File.WriteAllBytes(_finalPath, Full.AsSpan(0, 1000).ToArray());

        TestDownloadCache.GetOrDownload(Url, _cacheFolder, Download);

        _downloads.ShouldBe(1);
        File.ReadAllBytes(_finalPath).ShouldBe(Full);
    }

    [Test]
    public void ArchiveThatNoLongerMatchesItsHash_IsDownloadedAgain()
    {
        TestDownloadCache.GetOrDownload(Url, _cacheFolder, Download);
        File.WriteAllBytes(_finalPath, Full.AsSpan(0, Full.Length / 2).ToArray());

        TestDownloadCache.GetOrDownload(Url, _cacheFolder, Download);

        _downloads.ShouldBe(2);
        File.ReadAllBytes(_finalPath).ShouldBe(Full);
    }

    [Test]
    public void LeftoverTempFile_IsNeverUsedAsTheArchive()
    {
        Directory.CreateDirectory(_cacheFolder);
        var leftover = Path.Combine(_cacheFolder, "Archive.zip.0123456789abcdef" + TestDownloadCache.PartialSuffix);
        File.WriteAllBytes(leftover, Full.AsSpan(0, 1000).ToArray());

        TestDownloadCache.GetOrDownload(Url, _cacheFolder, Download);

        _downloads.ShouldBe(1);
        File.ReadAllBytes(_finalPath).ShouldBe(Full);
    }

    [Test]
    public void WriterThatLosesTheRename_UsesTheWinnersFile()
    {
        var winner = RandomNumberGenerator.GetBytes(1024);

        var path = TestDownloadCache.GetOrDownload(Url, _cacheFolder, (_, target) =>
        {
            File.WriteAllBytes(target, Full);
            // A concurrent writer commits first: hash, then the archive (the cache's own order).
            File.WriteAllText(TestDownloadCache.HashPath(_finalPath), Sha256(winner));
            File.WriteAllBytes(_finalPath, winner);
            return Task.CompletedTask;
        });

        path.ShouldBe(_finalPath);
        File.ReadAllBytes(_finalPath).ShouldBe(winner);
        Directory.GetFiles(_cacheFolder, "*" + TestDownloadCache.PartialSuffix).ShouldBeEmpty();
    }
}
