using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Models;
using Registry.Web.Services.Objects;
using Registry.Web.Test.Baselines;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <c>GET export</c>: the GeoTIFF libddb wrote to a scratch file is sent from the file while it is read, never loaded
/// in memory whole, with the headers of a file download (type, length, attachment name). The scratch file is gone
/// once the response ends, also when the export fails, when the client goes away during the download and when
/// several exports run at once.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~RasterExportHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class RasterExportHttpTests : DatasetAccessFixture
{
    private const string Ds = "/orgs/" + PrivateOrg + "/ds/" + PrivateDs;
    private const string Raster = "ortho.tif";
    private const long MiB = 1024 * 1024;

    // The bytes of the export the rescale "0,<seed>" asks for (each export of a test has its own)
    private long _size;
    private Exception? _failAfterWriting;

    private string TempFolder => Path.Combine(Factory.RootPath, "temp");

    [OneTimeSetUp]
    public async Task IndexTheRaster()
    {
        var internalRef = await WithContextAsync(context =>
            Task.FromResult(context.Datasets.Single(d => d.Slug == PrivateDs).InternalRef));
        using var scope = Factory.Services.CreateScope();
        var folder = scope.ServiceProvider.GetRequiredService<IDdbManager>().Get(PrivateOrg, internalRef).DatasetFolderPath;
        Factory.DdbWrapper.Setup(d => d.GetEntry(folder, Raster))
            .Returns(new Entry { Path = Raster, Hash = "raster-export-hash", Type = EntryType.GeoImage, Size = 1 });
    }

    [SetUp]
    public void ExportWhatTheTestSays()
    {
        _size = 1024;
        _failAfterWriting = null;
        Factory.DdbWrapper.Invocations.Clear();
        Factory.DdbWrapper.Setup(d => d.GetRasterValueInfo(It.IsAny<string>(), It.IsAny<RasterAnalysisOptions?>()))
            .Returns("""{"pixelCount": 100, "validPixelCount": 100}""");
        Factory.DdbWrapper.Setup(d => d.ExportRaster(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Callback((string _, string output, string? _, string? _, string? _, string? _, string? _,
                string? rescale) =>
            {
                WriteExport(output, _size, SeedOf(rescale));
                if (_failAfterWriting != null)
                    throw _failAfterWriting;
            });
    }

    private static int SeedOf(string? rescale) => rescale is { } r ? int.Parse(r.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture) : 0;

    private static byte ByteAt(long offset, int seed) => (byte)((offset * 31 + seed * 7) % 251);

    private static void WriteExport(string path, long size, int seed)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        var buffer = new byte[MiB];
        for (long offset = 0; offset < size;)
        {
            var count = (int)Math.Min(buffer.Length, size - offset);
            for (var i = 0; i < count; i++)
                buffer[i] = ByteAt(offset + i, seed);
            file.Write(buffer, 0, count);
            offset += count;
        }
    }

    private static void ShouldBeTheExport(byte[] body, long size, int seed)
    {
        body.LongLength.ShouldBe(size);
        for (long i = 0; i < size; i++)
            if (body[i] != ByteAt(i, seed))
                Assert.Fail($"The byte at {i} is {body[i]}, the export wrote {ByteAt(i, seed)}");
    }

    private static HttpRequestMessage Export(int seed = 1) =>
        Request(HttpMethod.Get, $"{Ds}/export?path={Raster}&rescale=0%2C{seed}", Caller.Owner);

    private void ShouldHaveNoScratchFile() =>
        (Directory.Exists(TempFolder) ? Directory.GetFiles(TempFolder) : []).ShouldBeEmpty();

    [Test]
    public async Task TheExport_IsSentWithTheHeadersOfAFileDownload_AndItsScratchFileIsGone()
    {
        using var response = await Client.SendAsync(Export(seed: 3));
        var body = await response.Content.ReadAsByteArrayAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/tiff");
        response.Content.Headers.ContentLength.ShouldBe(1024);
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        response.Content.Headers.ContentDisposition.FileName.ShouldBe("ortho_export.tif");
        response.Content.Headers.ContentDisposition.FileNameStar.ShouldBe("ortho_export.tif");
        response.Headers.AcceptRanges.ShouldBeEmpty();
        ShouldBeTheExport(body, 1024, 3);
        ShouldHaveNoScratchFile();
    }

    // A large export is read from its file while it is sent: the request allocates a small part of its size (it
    // allocated the whole export, and more, when the file was read into memory first)
    [Test]
    public async Task ALargeExport_IsNotLoadedInMemory()
    {
        _size = 64 * MiB;
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);

        using var response = await Client.SendAsync(Export(seed: 5), HttpCompletionOption.ResponseHeadersRead);
        await using (var body = await response.Content.ReadAsStreamAsync())
            await body.CopyToAsync(Stream.Null);

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentLength.ShouldBe(64 * MiB);
        // The export itself (the fake libddb) allocates its 1 MiB buffer; the request and the response a few more
        allocated.ShouldBeLessThan(16 * MiB);
        ShouldHaveNoScratchFile();
    }

    [Test]
    public async Task AnExportThatFailsAfterWritingPartOfItsFile_LeavesNoScratchFile()
    {
        _size = 3 * MiB;
        _failAfterWriting = new DdbException("Cannot write the GeoTIFF: no space left on device");

        using var response = await Client.SendAsync(Export());

        response.IsSuccessStatusCode.ShouldBeFalse();
        Factory.DdbWrapper.Invocations.ShouldContain(i => i.Method.Name == nameof(IDdbWrapper.ExportRaster));
        ShouldHaveNoScratchFile();
    }

    [Test]
    public async Task ConcurrentExports_EachSendItsOwnFile_AndLeaveNoScratchFile()
    {
        _size = 2 * MiB + 17;
        var seeds = Enumerable.Range(1, 8).ToArray();

        var bodies = await Task.WhenAll(seeds.Select(async seed =>
        {
            using var response = await Client.SendAsync(Export(seed));
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            return (Seed: seed, Body: await response.Content.ReadAsByteArrayAsync());
        }));

        foreach (var (seed, body) in bodies)
            ShouldBeTheExport(body, _size, seed);
        ShouldHaveNoScratchFile();
    }

    // The client goes away during the download: the file result stops writing and closes the stream, which deletes
    // the scratch file (the response of the export is that result)
    [Test]
    public async Task AClientThatGoesAwayDuringTheDownload_LeavesNoScratchFile()
    {
        _size = 4 * MiB;
        using var scope = Factory.Services.CreateScope();
        using var aborted = new CancellationTokenSource();
        // The request of the owner, whose response body is a client that leaves after the first bytes
        var http = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            RequestAborted = aborted.Token,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, UserIds[OwnerUser])], "Test")),
            Response = { Body = new GoneAfterFirstWrite(aborted) }
        };
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;

        var export = await scope.ServiceProvider.GetRequiredService<RasterAnalysisService>()
            .ExportRasterAsync(PrivateOrg, PrivateDs, Raster, new RasterVisualization(Rescale: "0,1"));
        Directory.GetFiles(TempFolder).Length.ShouldBe(1);
        var result = new FileStreamResult(export.Data, export.ContentType) { FileDownloadName = export.Name };

        try
        {
            await result.ExecuteResultAsync(new ActionContext(http, new RouteData(), new ActionDescriptor()));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // What the server sees of a client that went away
        }

        aborted.IsCancellationRequested.ShouldBeTrue();
        ShouldHaveNoScratchFile();
    }

    // A response body whose client leaves after the first bytes: the abort is signalled and every later write fails
    private sealed class GoneAfterFirstWrite(CancellationTokenSource aborted) : Stream
    {
        private bool _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Gone();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Gone();
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Gone();
            return Task.CompletedTask;
        }

        private void Gone()
        {
            if (_written)
                throw new IOException("The client is gone");
            _written = true;
            aborted.Cancel();
        }
    }
}
