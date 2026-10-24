#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Moq;
using System.Text.Json;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Shouldly;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Test;

/// <summary>
/// REG-08: listings are deserialized in place from libddb's UTF-8 buffer (<see cref="NativeJson"/>),
/// callers that need paths, hashes or types get <see cref="EntrySummary"/> rows without properties
/// and geometries, and <see cref="DDB.Find"/> hands folder, name and type filters to DroneDB.
/// The payloads mirror DDBListEntries output for 50 000 GeoImage entries.
/// </summary>
[TestFixture]
public class ListingMarshallingTests : TestBase
{
    private const int Entries = 50_000;
    private const string Hash = "abababababababababababababababababababababababababababababababab";

    private static string Path(int i) => $"dir{i % 100}/IMG_{i:D5}.JPG";

    private static byte[] FullListing()
    {
        var sb = new StringBuilder(Entries * 520);
        sb.Append('[');
        for (var i = 0; i < Entries; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"path\":\"").Append(Path(i)).Append("\",\"hash\":\"").Append(Hash).Append("\",")
                .Append("\"type\":3,\"properties\":{\"width\":4000,\"height\":3000,\"make\":\"DJI\",\"model\":\"FC6310\",")
                .Append("\"captureTime\":1600000000000.0,\"orientation\":1,\"sensor\":\"x\",\"focalLength\":8.8,")
                .Append("\"cameraYaw\":1.0,\"cameraPitch\":-90.0,\"cameraRoll\":0.0},")
                .Append("\"mtime\":1700000000,\"size\":8000000,\"depth\":1,")
                .Append("\"point_geom\":{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[9.1,45.4,120.0]}}}");
        }
        return Encoding.UTF8.GetBytes(sb.Append(']').ToString());
    }

    private static byte[] SummaryListing()
    {
        var sb = new StringBuilder(Entries * 140);
        sb.Append('[');
        for (var i = 0; i < Entries; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"depth\":1,\"hash\":\"").Append(Hash).Append("\",\"mtime\":1700000000,\"path\":\"")
                .Append(Path(i)).Append("\",\"size\":8000000,\"type\":3}");
        }
        return Encoding.UTF8.GetBytes(sb.Append(']').ToString());
    }

    /// <summary>A null-terminated copy of <paramref name="bytes"/> in unmanaged memory.</summary>
    private sealed class NativeBuffer : IDisposable
    {
        public NativeBuffer(byte[] bytes)
        {
            Length = bytes.Length;
            Pointer = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, Pointer, bytes.Length);
            Marshal.WriteByte(Pointer, bytes.Length, 0);
        }

        public IntPtr Pointer { get; }
        public int Length { get; }
        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    private static (double allocatedMb, double ms, int count) Measure<T>(Func<List<T>> run)
    {
        run(); // warm-up
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        var list = run();
        sw.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        return (allocated / (1024.0 * 1024), sw.Elapsed.TotalMilliseconds, list.Count);
    }

    [Test]
    public void NativeJson_FullListing_SkipsTheStringCopy_SummariesAllocateATenth()
    {
        using var full = new NativeBuffer(FullListing());
        using var summary = new NativeBuffer(SummaryListing());

        // What NativeDdbWrapper did before: UTF-16 copy of the whole payload, then parse
        var (stringMb, stringMs, stringCount) =
            Measure(() => JsonSerializer.Deserialize<List<Entry>>(Marshal.PtrToStringUTF8(full.Pointer)!, NativeJson.Options)!);
        var (fullMb, fullMs, fullCount) = Measure(() => NativeJson.Deserialize<List<Entry>>(full.Pointer)!);
        var (summaryMb, summaryMs, summaryCount) =
            Measure(() => NativeJson.Deserialize<List<EntrySummary>>(summary.Pointer)!);

        var payloadMb = full.Length / (1024.0 * 1024);
        TestContext.WriteLine($"full payload {payloadMb:F1} MB, summary payload {summary.Length / (1024.0 * 1024):F1} MB, {Entries} entries");
        TestContext.WriteLine($"string + parse:         {stringMs,5:F0} ms {stringMb,6:F0} MB ({stringMb / payloadMb:F1}x full payload)");
        TestContext.WriteLine($"NativeJson, Entry:      {fullMs,5:F0} ms {fullMb,6:F0} MB ({fullMb / payloadMb:F1}x)");
        TestContext.WriteLine($"NativeJson, summaries:  {summaryMs,5:F0} ms {summaryMb,6:F0} MB ({summaryMb / payloadMb:F1}x)");

        stringCount.ShouldBe(Entries);
        fullCount.ShouldBe(Entries);
        summaryCount.ShouldBe(Entries);
        (stringMb - fullMb).ShouldBeGreaterThan(payloadMb, "the UTF-16 copy (2 bytes per payload byte) is gone");
        summaryMb.ShouldBeLessThan(stringMb / 10, "summaries skip properties, geometries and their boxing");
    }

    [Test]
    public void NativeJson_RoundTripsLikeTheStringPath()
    {
        var bytes = FullListing();
        using var buffer = new NativeBuffer(bytes);

        var expected = JsonSerializer.Deserialize<List<Entry>>(Encoding.UTF8.GetString(bytes), NativeJson.Options)!;
        var actual = NativeJson.Deserialize<List<Entry>>(buffer.Pointer)!;

        actual.Count.ShouldBe(expected.Count);
        // Entries hold JSON documents: compare what they write
        NativeJson.Serialize(actual[123]).ShouldBe(NativeJson.Serialize(expected[123]));
        NativeJson.Serialize(actual[^1]).ShouldBe(NativeJson.Serialize(expected[^1]));
    }

    [Test]
    public void NativeJson_NullOrEmpty_IsDefault()
    {
        NativeJson.Deserialize<List<Entry>>(IntPtr.Zero).ShouldBeNull();
        using var empty = new NativeBuffer([]);
        NativeJson.Deserialize<List<Entry>>(empty.Pointer).ShouldBeNull();
        using var nul = new NativeBuffer("null"u8.ToArray());
        NativeJson.Deserialize<List<Entry>>(nul.Pointer).ShouldBeNull();
        using var multiByte = new NativeBuffer(Encoding.UTF8.GetBytes("[{\"path\":\"città/日本.jpg\",\"type\":6}]"));
        NativeJson.Deserialize<List<EntrySummary>>(multiByte.Pointer)![0].Path.ShouldBe("città/日本.jpg");
    }

    // ---- DDB.Find: one filtered native call, folder resolved like the rest of DDB ------------

    private string _root = null!;
    private Mock<IDdbWrapper> _wrapper = null!;

    [SetUp]
    public void Setup()
    {
        _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reg08-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _wrapper = new Mock<IDdbWrapper>(MockBehavior.Strict);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    [TestCase(null, "")]
    [TestCase("", "")]
    [TestCase(".", "")]
    [TestCase("./photos/", "photos")]
    [TestCase("photos/../maps\\2024", "maps/2024")]
    public void Find_PassesTheDatasetRelativeFolderAndFilters(string? path, string expectedFolder)
    {
        var entry = new Entry { Path = "maps/2024/a.jpg", Type = EntryType.GeoImage };
        _wrapper.Setup(w => w.ListEntries(_root, It.IsAny<EntryQuery>())).Returns([entry]);

        var query = new EntryQuery(path, false, "*.jpg", [EntryType.GeoImage]);
        new DDB(_root, _wrapper.Object).Find(query).ShouldBe([entry]);

        var expected = query with { Path = expectedFolder };
        _wrapper.Verify(w => w.ListEntries(_root, expected), Times.Once);
        _wrapper.VerifyNoOtherCalls();
    }

    [Test]
    public void FindSummaries_UsesTheSummaryListing()
    {
        var root = EntryQuery.All with { Path = "" };
        _wrapper.Setup(w => w.ListEntrySummaries(_root, root)).Returns([]);

        new DDB(_root, _wrapper.Object).FindSummaries(EntryQuery.All).ShouldBeEmpty();

        _wrapper.VerifyAll();
        _wrapper.VerifyNoOtherCalls();
    }

    [Test]
    public void Find_OutsideTheDataset_Throws()
    {
        Should.Throw<Exception>(() => new DDB(_root, _wrapper.Object).Find(EntryQuery.Under("../other")));
        _wrapper.VerifyNoOtherCalls();
    }
}
