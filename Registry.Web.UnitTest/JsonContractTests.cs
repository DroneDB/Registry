#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Registry.Ports.Json;
using Registry.Test.Common;
using Registry.Web.Test.Baselines;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Shouldly;
using JsonConvert = Newtonsoft.Json.JsonConvert;

namespace Registry.Web.Test;

/// <summary>
/// The generated contracts and what they read from disk and from native memory. A wire type missing from
/// <see cref="RegistryJsonContext"/> or <see cref="DdbJsonContext"/> would be served by reflection: slower, and
/// invisible until a trimmed build. The files and cache records an older Registry wrote with Json.NET (PascalCase,
/// or camelCase through explicit names) are read as they are, and the ones written now are read by Json.NET (a
/// downgrade needs no migration).
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public sealed class JsonContractTests
{
    [Test]
    public void EveryDtoOfTheApi_IsInAGeneratedContract()
    {
        var resolvers = new Microsoft.AspNetCore.Mvc.JsonOptions().JsonSerializerOptions;
        var missing = WireTypes.Discover()
            .Where(t => t.Namespace?.StartsWith("Registry.Web.Models", StringComparison.Ordinal) == true ||
                        t.Namespace == "Registry.Ports.DroneDB")
            .Where(t => t.IsPublic)
            .Where(t => !Excluded.Contains(t.FullName!))
            .Where(t => !InAContract(t, resolvers))
            .Select(t => t.FullName)
            .ToList();

        missing.ShouldBeEmpty("add a [JsonSerializable] for each to RegistryJsonContext (or DdbJsonContext for a port type)");
    }

    // Not wire types: streams, request-time state, configuration binding, framework-owned shapes
    private static readonly HashSet<string> Excluded =
    [
        "Registry.Web.Models.DTO.FileStreamDescriptor", "Registry.Web.Models.DTO.StreamableFileDescriptor",
        "Registry.Web.Models.DTO.TagConverter", "Registry.Web.Models.ControllerBaseEx", "Registry.Web.Models.JwtDescriptor",
        "Registry.Web.Models.DatasetScope", "Registry.Web.Models.ReadableObject", "Registry.Web.Models.DatasetListQuery",
        "Registry.Web.Models.IndexPayload", "Registry.Web.Models.RescanFilter", "Registry.Web.Models.DTO.IsBatchReadyResult",
        "Registry.Ports.DroneDB.DdbProcessSettings", "Registry.Ports.DroneDB.EntryQuery", "Registry.Ports.DroneDB.RescanOptions", "Registry.Ports.DroneDB.BuildOptions",
        "Registry.Ports.DroneDB.SplatTrainingOptions",
        "Registry.Ports.DroneDB.FileHashHint", "Registry.Ports.DroneDB.AddItem", "Registry.Ports.DroneDB.DdbCapabilities",
        "Registry.Ports.DroneDB.DdbEffectiveConfig", "Registry.Ports.DroneDB.DdbCancellation",
        "Registry.Ports.DroneDB.DdbOperation", "Registry.Ports.DroneDB.BatchAddItemError",
        "Registry.Ports.DroneDB.BatchAddUnchangedItem", "Registry.Ports.DroneDB.PendingBuildInfo",
        // The adapter reads libddb's answer by hand (plain path or object), the type never crosses a wire
        "Registry.Ports.DroneDB.ResolvedBuildFile"
    ];

    private static bool InAContract(Type type, JsonSerializerOptions options)
    {
        var contract = (IJsonTypeInfoResolver)RegistryJsonContext.Default;
        var ddb = (IJsonTypeInfoResolver)DdbJsonContext.Default;
        return contract.GetTypeInfo(type, options) != null || ddb.GetTypeInfo(type, options) != null;
    }

    [Test]
    public void ATinyLibddbOutput_CostsLessThanAKilobyte_NotTheTwoHundredThePreviousReaderAllocated()
    {
        using var output = new NativeOutput("{\"removed\":1}");
        for (var i = 0; i < 2000; i++)
            NativeJson.Deserialize<JsonElement>(output.Pointer);

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int calls = 1000;
        for (var i = 0; i < calls; i++)
            NativeJson.Deserialize<JsonElement>(output.Pointer);
        var perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)calls;

        TestContext.Out.WriteLine($"{perCall:F0} bytes per call");
        perCall.ShouldBeLessThan(1024, "the reader allocated 200 KB per call (a 64 Ki-char StreamReader buffer)");
    }

    [Test]
    public void NativeJson_ReadsMultiByteTextAndNullsInPlace()
    {
        using var multiByte = new NativeOutput("[{\"path\":\"città/日本.jpg\",\"type\":6}]");
        NativeJson.Deserialize<List<EntrySummary>>(multiByte.Pointer)![0].Path.ShouldBe("città/日本.jpg");

        NativeJson.Deserialize<List<Entry>>(IntPtr.Zero).ShouldBeNull();
        using var empty = new NativeOutput("");
        NativeJson.Deserialize<List<Entry>>(empty.Pointer).ShouldBeNull();
        using var nul = new NativeOutput("null");
        NativeJson.Deserialize<List<Entry>>(nul.Pointer).ShouldBeNull();
    }

    /// <summary>
    /// libddb flags a pending build that waits for missing files; a libddb from before the flag leaves it out, which
    /// reads as a build a retry can progress (as every pending build was then).
    /// </summary>
    [Test]
    public void APendingBuild_WaitsForFilesOnlyWhenLibddbSaysSo()
    {
        using var output = new NativeOutput(
            "[{\"hash\":\"h1\",\"path\":\"model.obj\",\"missingDependencies\":[\"model.mtl\"],\"lastAttempt\":1," +
            "\"retryAfter\":61,\"awaitingFiles\":true}," +
            "{\"hash\":\"h2\",\"path\":\"roads.shp\",\"missingDependencies\":[],\"lastAttempt\":1,\"retryAfter\":61}]");

        var pending = NativeJson.Deserialize<PendingBuildInfo[]>(output.Pointer)!;

        pending.Select(p => (p.Path, p.AwaitingFiles)).ShouldBe([("model.obj", true), ("roads.shp", false)]);
    }

    [Test]
    public void ALibddbDocument_OutlivesTheNativeBuffer()
    {
        JsonElement document;
        using (var output = new NativeOutput("{\"stac_version\":\"1.0.0\",\"nested\":{\"point_geom\":[1.5,2]}}"))
            document = NativeJson.Deserialize<JsonElement>(output.Pointer);

        // The buffer is freed and overwritten: the document owns its bytes, and keeps libddb's field names
        document.GetProperty("stac_version").GetString().ShouldBe("1.0.0");
        document.GetProperty("nested").GetProperty("point_geom")[0].GetDouble().ShouldBe(1.5);
    }

    [Test]
    public void APushSessionJsonNetWrote_IsReadAsItIs_AndTheOneWrittenNowIsReadByJsonNet()
    {
        var session = new PushSession
        {
            OurChecksum = "abc",
            Delta = new Delta
            {
                Adds = [new AddAction("a.txt", "h1")], Removes = [new RemoveAction("b.txt", "h2")],
                MetaAdds = ["m1"], MetaRemoves = []
            }
        };
        // Json.NET with no settings and the explicit names the type had: how an older Registry wrote the file
        var legacy = """{"ourChecksum":"abc","delta":{"adds":[{"path":"a.txt","hash":"h1"}],"removes":[{"path":"b.txt","hash":"h2"}],"metaAdds":["m1"],"metaRemoves":[]}}""";

        var read = ApiJson.Deserialize<PushSession>(legacy)!;
        read.OurChecksum.ShouldBe("abc");
        read.Delta.Adds.Single().Path.ShouldBe("a.txt");
        read.Delta.Removes.Single().Hash.ShouldBe("h2");
        read.Delta.MetaAdds.ShouldBe(["m1"]);

        ApiJson.Serialize(session).ShouldBe(legacy);
        var back = JsonConvert.DeserializeObject<PushSession>(ApiJson.Serialize(session))!;
        back.OurChecksum.ShouldBe("abc");
        back.Delta.Adds.Single().Hash.ShouldBe("h1");
    }

    [Test]
    public void TheStampTheCliPushes_IsRead_AndTheInitAnswerIsWhatTheCliParses()
    {
        // ddb push sends the stamp as JSON in a form field and reads the answer with nlohmann (DroneDB src/library/pushmanager.cpp)
        var stamp = ApiJson.Deserialize<StampDto>(
            """{"checksum":"c0ffee","entries":[{"path":"a.txt","hash":"h1"},{"path":"sub","hash":""}],"meta":["1b4e28ba-2fa1-11d2-883f-0016d3cca427"]}""")!;

        stamp.Checksum.ShouldBe("c0ffee");
        stamp.Entries.ShouldNotBeNull().Select(e => e["path"]).ShouldBe(["a.txt", "sub"]);
        stamp.Meta.ShouldBe(["1b4e28ba-2fa1-11d2-883f-0016d3cca427"]);

        ApiJson.Serialize(new PushInitResultDto { NeededFiles = ["a.txt"], NeededMeta = [], PullRequired = false, Token = "t" })
            .ShouldBe("""{"neededFiles":["a.txt"],"neededMeta":[],"pullRequired":false,"token":"t"}""");
    }

    [Test]
    public void AChunkManifestJsonNetWrote_IsReadAsItIs()
    {
        // Json.NET's default: member names as the type spells them (PascalCase)
        var legacy = """{"Path":"sub/a.tif","TotalSize":1000,"TotalChunks":4,"ChunkSize":250,"UserId":"u1"}""";

        var manifest = ApiJson.Deserialize<ChunkManifest>(legacy)!;

        manifest.ShouldBe(new ChunkManifest("sub/a.tif", 1000, 4, 250, "u1"));
        JsonConvert.DeserializeObject<ChunkManifest>(ApiJson.Serialize(manifest)).ShouldBe(manifest);
    }

    [Test]
    public void ACachedRecordJsonNetWrote_IsReadAsItIs()
    {
        // The STAC listing cache: a DTO with explicit names, a collection of documents
        var legacy = """{"collections":[{"id":"o/d","type":"Collection"}],"links":[{"href":"h","rel":"self"}],"numberMatched":1,"numberReturned":1}""";

        var listing = ApiJson.Deserialize<StacCollectionsDto>(legacy)!;

        listing.Collections.ShouldNotBeNull().Count().ShouldBe(1);
        listing.NumberMatched.ShouldBe(1);
        listing.Links.ShouldHaveSingleItem().Relationship.ShouldBe("self");
        JsonConvert.DeserializeObject<StacCollectionsDto>(ApiJson.Serialize(listing))!.NumberReturned.ShouldBe(1);
    }

    /// <summary>A null-terminated UTF-8 copy of a text in unmanaged memory.</summary>
    private sealed class NativeOutput : IDisposable
    {
        public NativeOutput(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Pointer = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, Pointer, bytes.Length);
            Marshal.WriteByte(Pointer, bytes.Length, 0);
        }

        public IntPtr Pointer { get; }

        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }
}
