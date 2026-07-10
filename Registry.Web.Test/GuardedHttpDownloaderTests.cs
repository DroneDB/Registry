#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Import;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Tests for <see cref="GuardedHttpDownloader"/>: the size cap (fail-fast + incremental), the
/// low-speed guard and the probe (HEAD with GET fallback). SSRF is bypassed via
/// <see cref="ImportSettings.SsrfAllowPrivateNetworks"/> so the tests stay hermetic (no DNS).
/// </summary>
[TestFixture]
public class GuardedHttpDownloaderTests
{
    private static readonly Uri Url = new("http://download.test/data.laz");

    private static GuardedHttpDownloader DownloaderFor(HttpMessageHandler handler)
    {
        var guard = new SsrfGuard(new ImportSettings { SsrfAllowPrivateNetworks = true });
        return new GuardedHttpDownloader(guard, new StubFactory(handler),
            NullLogger<GuardedHttpDownloader>.Instance);
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "ddb-dltest-" + Guid.NewGuid().ToString("N"));

    [Test]
    public async Task DownloadAsync_WritesFile_WhenWithinCap()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("hello world")
        });
        var dl = DownloaderFor(handler);
        var dest = TempFile();

        try
        {
            var bytes = await dl.DownloadAsync(Url, dest, null, null,
                maxBytes: 1000, minSpeedBytesPerSec: 0, lowSpeedGraceSeconds: 0,
                diskSafetyMarginBytes: 0, progress: null, ct: CancellationToken.None);

            bytes.ShouldBe(11);
            (await File.ReadAllTextAsync(dest)).ShouldBe("hello world");
        }
        finally { TryDelete(dest); }
    }

    [Test]
    public async Task DownloadAsync_FailsFast_WhenContentLengthExceedsCap()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 100))
        });
        var dl = DownloaderFor(handler);
        var dest = TempFile();

        try
        {
            await Should.ThrowAsync<QuotaExceededException>(async () =>
                await dl.DownloadAsync(Url, dest, null, null,
                    maxBytes: 10, minSpeedBytesPerSec: 0, lowSpeedGraceSeconds: 0,
                    diskSafetyMarginBytes: 0, progress: null, ct: CancellationToken.None));
        }
        finally { TryDelete(dest); }
    }

    [Test]
    public async Task DownloadAsync_Throws_WhenStreamExceedsCap_WithoutContentLength()
    {
        // A non-seekable body reports no Content-Length, so the cap must be enforced incrementally.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ForwardOnlyStream(new byte[100]))
        });
        var dl = DownloaderFor(handler);
        var dest = TempFile();

        try
        {
            await Should.ThrowAsync<QuotaExceededException>(async () =>
                await dl.DownloadAsync(Url, dest, null, null,
                    maxBytes: 10, minSpeedBytesPerSec: 0, lowSpeedGraceSeconds: 0,
                    diskSafetyMarginBytes: 0, progress: null, ct: CancellationToken.None));
        }
        finally { TryDelete(dest); }
    }

    [Test]
    public async Task DownloadAsync_Throws_WhenTooSlow()
    {
        // 10 bytes, then a >1s stall before the next 10 bytes: with a 1s window and a 1 MB/s floor the
        // rolling low-speed guard must abort.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new SlowStream(chunkSize: 10, delayMs: 1200, maxReads: 3))
        });
        var dl = DownloaderFor(handler);
        var dest = TempFile();

        try
        {
            await Should.ThrowAsync<TimeoutException>(async () =>
                await dl.DownloadAsync(Url, dest, null, null,
                    maxBytes: 0, minSpeedBytesPerSec: 1_000_000, lowSpeedGraceSeconds: 1,
                    diskSafetyMarginBytes: 0, progress: null, ct: CancellationToken.None));
        }
        finally { TryDelete(dest); }
    }

    [Test]
    public async Task ProbeAsync_ReturnsSizeAndFileName()
    {
        var handler = new StubHandler(req =>
        {
            req.Method.ShouldBe(HttpMethod.Head);
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            resp.Content.Headers.ContentLength = 4096;
            resp.Content.Headers.ContentDisposition =
                new ContentDispositionHeaderValue("attachment") { FileName = "cloud.laz" };
            return resp;
        });
        var dl = DownloaderFor(handler);

        var probe = await dl.ProbeAsync(Url, null, null, CancellationToken.None);

        probe.Reachable.ShouldBeTrue();
        probe.SizeBytes.ShouldBe(4096);
        probe.SuggestedFileName.ShouldBe("cloud.laz");
    }

    [Test]
    public async Task ProbeAsync_FallsBackToGet_WhenHeadRejected()
    {
        var handler = new StubHandler(req =>
        {
            if (req.Method == HttpMethod.Head)
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            resp.Content.Headers.ContentLength = 77;
            return resp;
        });
        var dl = DownloaderFor(handler);

        var probe = await dl.ProbeAsync(Url, null, null, CancellationToken.None);

        probe.Reachable.ShouldBeTrue();
        probe.SizeBytes.ShouldBe(77);
    }

    [Test]
    public async Task ProbeAsync_NotReachable_WhenErrorStatus()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var dl = DownloaderFor(handler);

        var probe = await dl.ProbeAsync(Url, null, null, CancellationToken.None);

        probe.Reachable.ShouldBeFalse();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    #region Test doubles

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(responder(request));
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Read-only, non-seekable stream so <see cref="StreamContent"/> reports no length.</summary>
    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Delivers <paramref name="chunkSize"/> bytes per read, stalling after the first read.</summary>
    private sealed class SlowStream(int chunkSize, int delayMs, int maxReads) : Stream
    {
        private int _reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_reads >= maxReads) return 0;
            if (_reads > 0) await Task.Delay(delayMs, ct);
            _reads++;
            var n = Math.Min(chunkSize, buffer.Length);
            buffer.Span[..n].Clear();
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    #endregion
}
