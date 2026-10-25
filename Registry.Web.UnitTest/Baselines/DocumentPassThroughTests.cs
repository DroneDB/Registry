#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// What happens to a libddb document between libddb's output and the Hub ("no DateTime round
/// trip"). libddb's UTF-8 text is read by <see cref="NativeJson"/> (the production path), mapped like the
/// managers do, and written by the host's output formatter. The inputs carry what the contract fixtures and
/// the goldens do not: number forms (<c>1e-05</c>, <c>2.0</c>, a 20-digit integer, <c>-0.0</c>), strings that
/// look like dates, escapes, non-ASCII text, HTML characters, snake_case and PascalCase keys. The result is
/// compared with <c>Snapshots/wire/passthrough.json</c>: recorded on Json.NET, then re-recorded for System.Text.Json
/// the document is written as libddb
/// wrote it, not parsed and rewritten.
/// Update on purpose: <c>REGISTRY_UPDATE_SNAPSHOTS=1</c>, then review the diff.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class DocumentPassThroughTests : IDisposable
{
    private const string SnapshotName = "wire/passthrough.json";

    /// <summary>The type of a JSON document the ports return (the stack under test).</summary>
    private static readonly Type Document = typeof(JsonElement);

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private RegistryWebFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _factory = new RegistryWebFactory();

    /// <summary>NUnit disposes the fixture after its last test.</summary>
    public void Dispose() => _factory.Dispose();

    private const string Properties = """
        {"captureTime":1700000000.5,"altitude":100,"altitudeFraction":100.0,"exposure":1e-05,"huge":1.5E+20,"bigInt":12345678901234567890,
         "negativeZero":-0.0,"fraction":0.1,"longFraction":0.30000000000000004,"iso":"100","day":"2026-01-01",
         "instant":"2026-01-01T00:00:00.000Z","precise":"2026-01-01T00:00:00.1234567Z","offset":"2026-01-01T10:00:00+02:00",
         "local":"2026-01-01T10:00:00","unicode":"città 日本 😀","escapes":"a\"b\\c\n\t","html":"<&>'","slash":"a/b",
         "snake_case":1,"CamelKey":2,"flag":true,"none":null,"list":[1,2.0,"2026-01-01T00:00:00Z",null,[]],"nested":{"a":{"b":[{}]}}}
        """;

    /// <summary>libddb's entry listing (<c>DDBListEntries</c>), one entry with every kind of value in its properties.</summary>
    private static readonly string EntryList = $$$"""
        [{"path":"sub/a.jpg","hash":"0a1b2c","type":5,"mtime":1700000000,"size":1234,"depth":1,
          "properties":{{{Properties}}},
          "point_geom":{"type":"Point","coordinates":[11.123456789012345,45.0,100.0]},
          "polygon_geom":{"type":"Polygon","coordinates":[[[11.0,45.0,0],[11.5,45.0,0],[11.5,45.5,0],[11.0,45.0,0]]]}},
         {"path":"b.txt","hash":"","type":1,"mtime":1700000001.25,"size":0,"depth":0,"properties":{},"point_geom":null,"polygon_geom":null}]
        """;

    /// <summary>A STAC item as libddb emits it (data only).</summary>
    private const string StacItem = """
        {"stac_version":"1.0.0","type":"Feature","id":"a.jpg","bbox":[11.0,45.0,11.5,45.5],
         "geometry":{"type":"Point","coordinates":[11.1,45.2]},
         "properties":{"datetime":"2026-01-01T00:00:00.000Z","gsd":0.05,"proj:epsg":32632,"eo:cloud_cover":0.0,"updated":"2026-01-01"},
         "assets":{"data":{"href":"a.jpg","type":"image/jpeg","roles":["data"],"file:size":1234}},"links":[]}
        """;

    /// <summary>The values a meta key can hold (<c>DDBMetaGet</c>).</summary>
    private static readonly string[] MetaValues =
    [
        "{\"note\":\"tree\",\"level\":2,\"ratio\":0.5,\"when\":\"2026-01-01T00:00:00.000Z\",\"geo\":{\"type\":\"Point\",\"coordinates\":[1.0,2.0]}}",
        "[\"a\",\"2026-01-01\",3,4.0]", "\"plain text\"", "42", "1e3", "2.50", "true", "null", "[]", "{}",
        "\"2026-01-01T00:00:00.000Z\"", "12345678901234567890"
    ];

    [Test]
    public async Task LibddbDocuments_ReachTheHubAsRecorded()
    {
        var recorded = new JsonObject();

        // entries: NativeJson -> Entry -> EntryDto (the manager's mapping) -> the output formatter
        var entries = Read<List<Entry>>(EntryList)!;
        recorded["entries"] = new JsonObject
        {
            ["input"] = JsonNode.Parse(EntryList),
            ["output"] = JsonNode.Parse(await MvcJson.WriteAsync(_factory.Services, entries.Select(e => e.ToDto()).ToList()))
        };

        // meta values, as a MetaDto
        var metas = new JsonObject();
        foreach (var value in MetaValues)
        {
            var meta = Read<Meta>($$"""{"id":"0a223495","data":{{value}},"mtime":1700000000}""")!;
            metas[value] = new JsonObject
            {
                ["output"] = JsonNode.Parse(await MvcJson.WriteAsync(_factory.Services, meta.ToDto()))
            };
        }

        recorded["meta"] = metas;

        // STAC data, as the document the port returns
        var stac = Read(Document, StacItem);
        recorded["stac"] = new JsonObject
        {
            ["input"] = JsonNode.Parse(StacItem),
            ["output"] = JsonNode.Parse(await MvcJson.WriteAsync(_factory.Services, stac!))
        };

        var failure = SnapshotFile.VerifyDocument(SnapshotName, recorded.ToJsonString(Indented));
        failure.ShouldBeNull(failure);
    }

    private static T? Read<T>(string json) => (T?)Read(typeof(T), json);

    private static object? Read(Type type, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var pointer = Marshal.AllocHGlobal(bytes.Length + 1);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Marshal.WriteByte(pointer, bytes.Length, 0);
            return typeof(NativeJson).GetMethod(nameof(NativeJson.Deserialize))!.MakeGenericMethod(type)
                .Invoke(null, [pointer]);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }
}
