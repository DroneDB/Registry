using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

/// <summary>
/// The build object of an entry (<c>DDBGetBuildArtifacts</c>; ddb.h "build
/// manifest"): libddb's one answer to "is it buildable, what state is its build in and
/// which artifacts exist".
/// </summary>
public sealed class EntryBuild
{
    /// <summary>The entry is built, by itself or as a sidecar of its main file.</summary>
    [JsonPropertyName("buildable")]
    public bool Buildable { get; set; }

    /// <summary>Dataset-relative path of the entry the artifacts belong to (the main file of a sidecar).</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>The content hash of <see cref="Source"/>.</summary>
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    /// <summary>
    /// The build key of <see cref="Source"/> (libddb's <c>build_layout.h</c> <c>buildKey</c>), which names its build
    /// folder and its URLs (<c>.../build/{key}/{path}</c>): the hash, or the hash and the digest of the georeference
    /// (<c>{hash}-{digest}</c>) for a model or a splat that has one, so the same file in two places has a build
    /// for each. Absent from a libddb that predates the build key: <see cref="BuildKey"/> then reads the hash.
    /// </summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>The key that names the build of <see cref="Source"/>: <see cref="Key"/>, else <see cref="Hash"/> (an older libddb).</summary>
    [JsonIgnore]
    public string? BuildKey => Key ?? Hash;

    /// <summary>The absolute build root of <see cref="Source"/>; it may not exist yet.</summary>
    [JsonPropertyName("root")]
    public string? Root { get; set; }

    [JsonPropertyName("state")]
    public BuildState State { get; set; }

    /// <summary>From the pending marker (a pending or building entry), dataset-relative names.</summary>
    [JsonPropertyName("missingDependencies")]
    public IReadOnlyList<string> MissingDependencies { get; set; } = [];

    /// <summary>
    /// The pending build waits for files the dataset lacks (its marker lists them in
    /// <see cref="MissingDependencies"/>): no timer retries it, adding one of those files releases it. libddb
    /// decides it, the same value as <see cref="PendingBuildInfo.AwaitingFiles"/>; false without a marker, and
    /// absent from an older libddb, which reads as false.
    /// </summary>
    [JsonPropertyName("awaitingFiles")]
    public bool AwaitingFiles { get; set; }

    /// <summary>Unix seconds of the last attempt, from the pending marker; null without one.</summary>
    [JsonPropertyName("lastAttempt")]
    public long? LastAttempt { get; set; }

    /// <summary>
    /// Unix seconds from which libddb retries the pending build (<see cref="LastAttempt"/> + its retry
    /// backoff; due when the current time is at or past it); null exactly when <see cref="LastAttempt"/>
    /// is. Required (it may be null): a libddb without the field fails deserialization.
    /// </summary>
    [JsonPropertyName("retryAfter")]
    [JsonRequired]
    public long? RetryAfter { get; set; }

    /// <summary>The artifacts that exist, the primary output first.</summary>
    [JsonPropertyName("artifacts")]
    public IReadOnlyList<BuildArtifact> Artifacts { get; set; } = [];
}
