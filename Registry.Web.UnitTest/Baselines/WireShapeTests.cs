using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The shape of every wire type, pinned before the System.Text.Json switch and unchanged after it.
/// Each public DTO and port model (the namespaces the Hub's answers are made of, plus every type an action
/// declares as a response or takes as a parameter) is built twice by <see cref="WirePopulator"/>: "full" (every
/// member filled: numbers whole and fractional, dates of both kinds, enums, nested objects, collections,
/// dictionaries with mixed-case keys, JSON documents) and "sparse" (every reference member null). Both are
/// written by the host's own MVC output formatter, so member names, null handling, enum form, date form,
/// number form, key order and key casing are compared with <c>Snapshots/wire/dto-shapes.json</c>, recorded
/// on Json.NET. The golden JSON tests are the endpoint-level half of the same proof.
/// Update on purpose: <c>REGISTRY_UPDATE_SNAPSHOTS=1 dotnet test Registry.Web.UnitTest --filter
/// "FullyQualifiedName~WireShapeTests"</c>, then review the diff.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class WireShapeTests : IDisposable
{
    private const string SnapshotName = "wire/dto-shapes.json";

    /// <summary>Types that cannot be written as JSON by design (streams, process-local state) or are not wire types.</summary>
    private static readonly HashSet<string> NotWireTypes =
    [
        "Registry.Web.Models.DTO.FileStreamDescriptor", "Registry.Web.Models.DTO.StreamableFileDescriptor",
        "Registry.Web.Models.DTO.TagConverter", "Registry.Web.Models.ControllerBaseEx",
        "Registry.Web.Models.JwtDescriptor", "Registry.Web.Models.DatasetScope", "Registry.Web.Models.ReadableObject",
        "Registry.Web.Models.DatasetListQuery", "Registry.Web.Models.IndexPayload", "Registry.Web.Models.RescanFilter",
        "Registry.Ports.DroneDB.DdbProcessSettings", "Registry.Ports.DroneDB.EntryQuery",
        "Registry.Ports.DroneDB.RescanOptions", "Registry.Ports.DroneDB.BuildOptions", "Registry.Ports.DroneDB.FileHashHint", "Registry.Ports.DroneDB.NormalizedEntryPath",
        "Registry.Ports.DroneDB.ResolvedBuildFile", "Registry.Ports.DroneDB.IndexFileState",
        "Registry.Web.Services.Adapters.BuildPendingService", "Registry.Web.Utilities.DatasetPath"
    ];

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private RegistryWebFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _factory = new RegistryWebFactory();

    /// <summary>NUnit disposes the fixture after its last test.</summary>
    public void Dispose() => _factory.Dispose();

    /// <summary>What a JSON document member holds on the stack under test (the one place that differs by stack).</summary>
    private static readonly Func<Type, string, object?> Literal = (_, json) => TestJson.Parse(json);

    public static IEnumerable<Type> Types() => WireTypes.Discover().Where(t => !NotWireTypes.Contains(t.FullName!));

    [Test]
    public async Task EveryWireType_IsWrittenAsRecorded()
    {
        var shapes = new JsonObject();
        foreach (var type in Types())
        {
            var populator = new WirePopulator(Literal);
            var entry = new JsonObject();
            foreach (var (name, full) in new[] { ("full", true), ("sparse", false) })
            {
                var value = populator.Populate(type, full, type.Name);
                if (value == null)
                {
                    entry[name] = null;
                    continue;
                }

                string json;
                try { json = await MvcJson.WriteAsync(_factory.Services, value); }
                catch (Exception ex) { throw new InvalidOperationException($"{type.FullName} {name}: {ex.Message}", ex); }
                entry[name] = JsonNode.Parse(json);
            }

            shapes[type.FullName!] = entry;
        }

        var failure = SnapshotFile.VerifyDocument(SnapshotName, shapes.ToJsonString(Indented));
        failure.ShouldBeNull(failure);
    }

    [Test]
    public void Discovery_FindsTheWireTypes()
    {
        // A discovery that silently finds nothing would make the snapshot test vacuous
        var names = Types().Select(t => t.FullName).ToList();
        names.Count.ShouldBeGreaterThan(150);
        names.ShouldContain("Registry.Web.Models.DTO.EntryDto");
        names.ShouldContain("Registry.Web.Models.DTO.OrganizationDto");
        names.ShouldContain("Registry.Ports.DroneDB.Entry");
    }

    [Test]
    public void Populator_FillsEveryMember_AndLeavesTheSparseOnesNull()
    {
        var full = (Registry.Web.Models.DTO.EntryDto)new WirePopulator(Literal).Populate(
            typeof(Registry.Web.Models.DTO.EntryDto), true)!;
        var sparse = (Registry.Web.Models.DTO.EntryDto)new WirePopulator(Literal).Populate(
            typeof(Registry.Web.Models.DTO.EntryDto), false)!;

        full.Path.ShouldNotBeNull();
        full.Properties.ShouldNotBeEmpty();
        full.PointGeometry.ShouldNotBeNull();
        sparse.Path.ShouldBeNull();
        sparse.Properties.ShouldBeNull();
        sparse.PointGeometry.ShouldBeNull();
    }
}

/// <summary>The wire types of the host: the DTO and port-model namespaces, and every type an action names.</summary>
internal static class WireTypes
{
    public static List<Type> Discover()
    {
        var web = typeof(Program).Assembly;
        var ports = typeof(Registry.Ports.DroneDB.Entry).Assembly;
        var set = new HashSet<Type>();

        foreach (var t in web.GetExportedTypes().Where(t => t.Namespace is "Registry.Web.Models.DTO" or "Registry.Web.Models.DTO.Ogc" or "Registry.Web.Models"))
            set.Add(t);
        foreach (var t in ports.GetExportedTypes().Where(t => t.Namespace == "Registry.Ports.DroneDB"))
            set.Add(t);

        foreach (var action in web.GetExportedTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t))
                     .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)))
        {
            foreach (var p in action.GetCustomAttributes<ProducesResponseTypeAttribute>())
                set.Add(Unwrap(p.Type));
            foreach (var p in action.GetParameters())
                set.Add(Unwrap(p.ParameterType));
        }

        return set
            .Where(t => (t.Assembly == web || t.Assembly == ports) && !t.IsNested)
            .Where(t => !t.IsEnum && !t.IsInterface && !t.IsAbstract && !t.IsGenericTypeDefinition)
            .Where(t => t.IsClass || t.IsValueType)
            .Where(t => !typeof(Exception).IsAssignableFrom(t) && !typeof(Attribute).IsAssignableFrom(t) &&
                        !typeof(Delegate).IsAssignableFrom(t) && !typeof(ControllerBase).IsAssignableFrom(t))
            .Where(WirePopulator.CanPopulate)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
    }

    private static Type Unwrap(Type t)
    {
        if (t.IsArray)
            return Unwrap(t.GetElementType()!);
        if (t.IsGenericType)
            return Unwrap(t.GetGenericArguments()[^1]);
        return t;
    }
}
