using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Registry.Ports.DroneDB;

/// <summary>
/// The build object of an entry (<c>DDBGetBuildArtifacts</c>, <c>DDBListEntriesEx</c>; ddb.h "build
/// manifest", XI-04): libddb's one answer to "is it buildable, what state is its build in and
/// which artifacts exist".
/// </summary>
public sealed class EntryBuild
{
    /// <summary>The entry is built, by itself or as a sidecar of its main file.</summary>
    [JsonProperty("buildable")]
    public bool Buildable { get; set; }

    /// <summary>Dataset-relative path of the entry the artifacts belong to (the main file of a sidecar).</summary>
    [JsonProperty("source")]
    public string? Source { get; set; }

    /// <summary>The hash of <see cref="Source"/>, which names its build (<c>.../build/{hash}/{path}</c>).</summary>
    [JsonProperty("hash")]
    public string? Hash { get; set; }

    /// <summary>The absolute build root of <see cref="Source"/>; it may not exist yet.</summary>
    [JsonProperty("root")]
    public string? Root { get; set; }

    [JsonProperty("state")]
    [JsonConverter(typeof(StringEnumConverter))]
    public BuildState State { get; set; }

    /// <summary>From the pending marker (a pending or building entry), dataset-relative names.</summary>
    [JsonProperty("missingDependencies")]
    public IReadOnlyList<string> MissingDependencies { get; set; } = [];

    /// <summary>Unix seconds of the last attempt, from the pending marker; null without one.</summary>
    [JsonProperty("lastAttempt")]
    public long? LastAttempt { get; set; }

    /// <summary>
    /// Unix seconds from which libddb retries the pending build (<see cref="LastAttempt"/> + its retry
    /// backoff; due when the current time is at or past it); null exactly when <see cref="LastAttempt"/>
    /// is. Required (it may be null): a libddb without the field fails deserialization.
    /// </summary>
    [JsonProperty("retryAfter", Required = Required.AllowNull)]
    public long? RetryAfter { get; set; }

    /// <summary>The artifacts that exist, the primary output first.</summary>
    [JsonProperty("artifacts")]
    public IReadOnlyList<BuildArtifact> Artifacts { get; set; } = [];
}
