#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// A bulk-download archive is hashed while it is written (<c>HashingWriteStream</c>), so the
/// task does not read the whole archive back for its SHA-256 (one more full read per archive on the
/// HDD RAID5). Measured with the process's read counter (<c>/proc/self/io</c> rchar) around the
/// tool's execution, with an archive writer that only writes (it reads no source file).
/// </summary>
[TestFixture]
[NonParallelizable]
[Platform("Linux")]
public class ArtifactHashingTests
{
    private const int ArchiveBytes = 64 * 1024 * 1024;

    private static long BytesRead() =>
        File.ReadLines("/proc/self/io").Where(l => l.StartsWith("rchar:", StringComparison.Ordinal))
            .Select(l => long.Parse(l["rchar:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture))
            .Single();

    [Test]
    public async Task BulkDownload_HashesTheArchiveWhileWritingIt_AndDoesNotReadItBack()
    {
        var work = Path.Combine(Path.GetTempPath(), "artifacthashing-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var content = new byte[ArchiveBytes];
            new Random(9).NextBytes(content);
            var zip = new Mock<IZipArchiveBuilder>();
            zip.Setup(z => z.ExpandPaths(It.IsAny<IDDB>(), It.IsAny<string[]?>())).Returns((["a.tif"], [], false));
            zip.Setup(z => z.WriteZipAsync(It.IsAny<IDDB>(), It.IsAny<string[]>(), It.IsAny<string[]>(), It.IsAny<bool>(),
                    It.IsAny<Stream>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()))
                .Returns<IDDB, string[], string[], bool, Stream, IProgress<long>?, CancellationToken>(
                    async (_, _, _, _, output, _, ct) =>
                    {
                        // Written in chunks, as the zip writer streams entries
                        for (var offset = 0; offset < content.Length; offset += 1024 * 1024)
                            await output.WriteAsync(content.AsMemory(offset, 1024 * 1024), ct);
                    });
            var ddb = new Mock<IDDB>();
            ddb.Setup(d => d.GetLocalPath(It.IsAny<string>())).Returns<string>(p => Path.Combine(work, "missing-" + p));
            var ctx = TestJobs.ExecutionContext(ddb.Object, workDir: work);
            var request = new HeavyToolRequest("bulk-download", "1", "o", "d", null,
                JsonDocument.Parse("""{"paths":["a.tif"],"archiveName":"x.zip"}""").RootElement);

            var before = BytesRead();
            var artifact = await new BulkDownloadTool(zip.Object)
                .ExecuteAsync(request, ctx, new Progress<HeavyToolProgress>(), CancellationToken.None);
            var read = BytesRead() - before;

            TestContext.Out.WriteLine($"Archive {ArchiveBytes:N0} bytes; read during the task: {read:N0} bytes");
            artifact.ShouldNotBeNull();
            artifact.SizeBytes.ShouldBe(ArchiveBytes);
            artifact.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(content)));
            read.ShouldBeLessThan(ArchiveBytes / 8, "the archive is not read back to hash it");
        }
        finally
        {
            Directory.Delete(work, true);
        }
    }
}
