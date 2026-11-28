using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Adapters.Archives;
using Registry.Web.Models.Configuration;
using Registry.Ports.Import;
using Registry.Web.Services.Import;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The scratch file of an archive import keeps the archive's extension so the extractor picks the format: the longest
/// suffix of the extractor's own list (<c>IArchiveExtractor.SupportedExtensions</c>, the one rule) the URL ends with.
/// </summary>
[TestFixture]
public class ArchiveUrlImportSourceTests
{
    private static readonly SharpCompressArchiveExtractor Extractor = new();

    [TestCase("https://host/data/flight.zip", ".zip")]
    [TestCase("https://host/data/flight.ZIP", ".zip")]
    [TestCase("https://host/data/flight.tar.gz", ".tar.gz")]
    [TestCase("https://host/data/flight.tgz", ".tgz")]
    [TestCase("https://host/data/flight.tar.bz2", ".tar.bz2")]
    [TestCase("https://host/data/flight.tar.xz", ".tar.xz")]
    [TestCase("https://host/data/flight.tbz2", ".tbz2")]
    [TestCase("https://host/data/flight.gz", ".gz")]
    [TestCase("https://host/data/flight.7z?token=a.b", ".7z")]
    [TestCase("https://host/data/flight.docx", ".docx")]
    [TestCase("https://host/data/flight", ".zip")]
    public void ArchiveExt_IsTheLongestSuffixOfTheExtractorsList(string url, string expected) =>
        ArchiveUrlImportSource.ArchiveExt(url, Extractor.SupportedExtensions).ShouldBe(expected);

    [Test]
    public void ArchiveExt_PicksTheCompoundSuffixWhateverTheOrderOfTheList() =>
        ArchiveUrlImportSource.ArchiveExt("https://host/a.tar.gz", [".gz", ".tar", ".tar.gz"]).ShouldBe(".tar.gz");

    [Test]
    public void ArchiveExt_FollowsTheList_NotACopyOfIt() =>
        ArchiveUrlImportSource.ArchiveExt("https://host/a.tar.zst", [".zst", ".tar.zst"]).ShouldBe(".tar.zst");

    [TestCase("https://host/data/flight.zip", "flight")]
    [TestCase("https://host/data/flight.tar.gz", "flight")]
    [TestCase("https://host/data/flight.tar.bz2", "flight")]
    [TestCase("https://host/data/flight.tgz", "flight")]
    [TestCase("https://host/data/flight.v2.7z", "flight.v2")]
    [TestCase("https://host/data/", "imported-dataset")]
    public void SuggestName_StripsTheLongestSuffixOfTheExtractorsList(string url, string expected) =>
        ArchiveUrlImportSource.SuggestName(new System.Uri(url), Extractor.SupportedExtensions).ShouldBe(expected);

    // FR-UPL-12, FR-UPL-13: the verify step of an archive URL is the probe of the downloader (HEAD, then GET for a server
    // that refuses HEAD, and the first bytes of the content)

    private static ArchiveUrlImportSource SourceFor(HttpMessageHandler handler)
    {
        var settings = new ImportSettings { SsrfAllowPrivateNetworks = true };
        var downloader = new GuardedHttpDownloader(new SsrfGuard(settings), new HandlerFactory(handler),
            TestHeadroom.Default);
        return new ArchiveUrlImportSource(Extractor, downloader, Microsoft.Extensions.Options.Options.Create(settings),
            Microsoft.Extensions.Options.Options.Create(new ProcessingPlatformSettings()), TestArchives.Extractor(Extractor), TestScratch.Provider);
    }

    private static JsonElement Params(string url = "http://download.test/flight.zip") =>
        JsonSerializer.SerializeToElement(new { url });

    private static HttpResponseMessage Zip(long? length = null)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0])
        };
        resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        if (length != null)
            resp.Content.Headers.ContentLength = length;
        return resp;
    }

    [Test]
    public async Task Probe_AnHtmlPage_IsNotReachable_WithTheArchiveMessage()
    {
        var source = SourceFor(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<!doctype html><html>Not found</html>", System.Text.Encoding.UTF8, "text/html")
        }));

        var probe = await source.ProbeAsync(Params(), CancellationToken.None);

        probe.Reachable.ShouldBeFalse();
        probe.Message.ShouldBe("The file is not a ZIP, TAR, 7z or RAR archive");
    }

    [Test]
    public async Task Probe_Head405AndGet200_IsReachable_WithTheContentLength()
    {
        var source = SourceFor(new Handler(req =>
        {
            if (req.Method == HttpMethod.Head)
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            return req.Headers.Range != null ? Zip() : Zip(length: 5_000_000);
        }));

        var probe = await source.ProbeAsync(Params(), CancellationToken.None);

        probe.Reachable.ShouldBeTrue();
        probe.EstimatedBytes.ShouldBe(5_000_000);
        probe.SuggestedName.ShouldBe("flight");
    }

    [Test]
    public async Task Probe_AnUnreachableUrl_IsNotReachable()
    {
        var source = SourceFor(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var probe = await source.ProbeAsync(Params(), CancellationToken.None);

        probe.Reachable.ShouldBeFalse();
        probe.Message.ShouldNotBeNull().ShouldContain("404");
    }

    /// <summary>
    /// The log of the download has one line per whole percent, while the progress follows every chunk read (red before:
    /// one line per chunk, thousands for a 100 MB archive).
    /// </summary>
    [Test]
    public async Task TheDownload_LogsALinePerPercent_NotPerChunk()
    {
        var archive = new System.IO.MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(archive, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using var entry = zip.CreateEntry("data.bin", System.IO.Compression.CompressionLevel.NoCompression).Open();
            entry.Write(new byte[4 * 1024 * 1024]);
        }

        var bytes = archive.ToArray();
        var source = SourceFor(new Handler(_ =>
        {
            // A network read returns a few KB at a time
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new SmallReads(new System.IO.MemoryStream(bytes), 16 * 1024))
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            resp.Content.Headers.ContentLength = bytes.Length;
            return resp;
        }));
        var reports = new System.Collections.Generic.List<ImportProgress>();
        var dest = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "archive-progress-" + Guid.NewGuid().ToString("N"));
        var writes = new Moq.Mock<Registry.Ports.Import.IImportWriteRecord>();
        writes.Setup(w => w.MayWrite(Moq.It.IsAny<string>())).Returns(true);
        try
        {
            await source.FetchAsync(Params(), dest, writes.Object, new SyncProgress(reports.Add), CancellationToken.None);
        }
        finally
        {
            if (System.IO.Directory.Exists(dest)) System.IO.Directory.Delete(dest, true);
        }

        var downloading = reports.Where(r => r.Phase == "downloading").ToArray();
        downloading.Length.ShouldBeGreaterThan(110, "the progress follows every chunk read");
        var lines = downloading.Select(r => r.Message).OfType<string>().ToArray();
        lines.Length.ShouldBeLessThanOrEqualTo(101);
        lines.Distinct().Count().ShouldBe(lines.Length);
        lines[^1].ShouldStartWith("Downloaded 4");
    }

    // A stream whose reads return at most maxRead bytes
    private sealed class SmallReads(System.IO.Stream inner, int maxRead) : System.IO.Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, maxRead));
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SyncProgress(Action<ImportProgress> report) : IProgress<ImportProgress>
    {
        public void Report(ImportProgress value) => report(value);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(responder(request));
    }

    private sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
