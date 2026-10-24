using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Registry.Ports.DroneDB;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// The JSON contract with libddb (RQ-14, D3, XI-09), source-generated: every type <see cref="NativeJson"/> reads from or
/// writes to libddb. The member names are libddb's (<c>point_geom</c>, <c>mtime</c>, <c>missingDependencies</c>), fixed on
/// the contract types with <c>[JsonPropertyName]</c>, so no naming policy can rename them. Metadata only: the
/// options carry converters (epoch seconds) and a text encoder that the generated fast path would not honour.
/// Registry's own wire (<c>RegistryJsonContext</c> in Registry.Web) chains this context for the port types it carries.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Entry))]
[JsonSerializable(typeof(EntrySummary))]
[JsonSerializable(typeof(BatchAddResult))]
[JsonSerializable(typeof(BatchAddedEntry))]
[JsonSerializable(typeof(BuildArtifact))]
[JsonSerializable(typeof(CopiedBuildArtifacts))]
[JsonSerializable(typeof(DdbCleanupResult))]
[JsonSerializable(typeof(Delta))]
[JsonSerializable(typeof(AddAction))]
[JsonSerializable(typeof(RemoveAction))]
[JsonSerializable(typeof(EntryBuild))]
[JsonSerializable(typeof(EntryBuildArtifacts))]
[JsonSerializable(typeof(EntryTypeInfo))]
[JsonSerializable(typeof(Meta))]
[JsonSerializable(typeof(MetaDump))]
[JsonSerializable(typeof(MetaListItem))]
[JsonSerializable(typeof(NormalizedEntryPath))]
[JsonSerializable(typeof(PendingBuildInfo))]
[JsonSerializable(typeof(RescanResult))]
[JsonSerializable(typeof(Stamp))]
[JsonSerializable(typeof(List<Entry>))]
[JsonSerializable(typeof(List<EntrySummary>))]
[JsonSerializable(typeof(List<EntryTypeInfo>))]
[JsonSerializable(typeof(List<RescanResult>))]
[JsonSerializable(typeof(List<MetaListItem>))]
[JsonSerializable(typeof(List<MetaDump>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(EntryBuildArtifacts[]))]
[JsonSerializable(typeof(PendingBuildInfo[]))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class DdbJsonContext : JsonSerializerContext;
