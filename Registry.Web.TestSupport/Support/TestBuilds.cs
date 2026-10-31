using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Moq;
using Registry.Ports.DroneDB;

namespace Registry.Web.Test.Support;

/// <summary>
/// libddb's build manifest in tests (<see cref="IDdbBuild.GetBuildArtifacts"/>): the build object of
/// an entry in each state, and a <see cref="Mock{IDDB}"/> answering the manifest call from them.
/// </summary>
public static class TestBuilds
{
    /// <summary>The build object of <paramref name="path"/> in a real dataset (one native call).</summary>
    public static EntryBuild? BuildOf(this IDDB ddb, string path) => ddb.GetBuildArtifacts([path])[0].Build;

    /// <summary>The build of <paramref name="path"/> is complete (libddb's "complete" state).</summary>
    public static bool IsBuildComplete(this IDDB ddb, string path) => ddb.BuildOf(path)?.State == BuildState.Complete;

    /// <summary>An entry that is not buildable.</summary>
    public static EntryBuildArtifacts NotBuildable(string path, EntryType type = EntryType.Generic,
        string? hash = "0123") =>
        new()
        {
            Path = path, Exists = true, Hash = hash, Type = type,
            Build = new EntryBuild { Buildable = false, State = BuildState.None }
        };

    /// <summary>
    /// A buildable entry with its own build in <paramref name="state"/> and these artifacts (paths relative
    /// to the build root <c>&lt;datasetFolder&gt;/.ddb/build/&lt;hash&gt;</c>, as libddb reports them).
    /// </summary>
    public static EntryBuildArtifacts Build(string datasetFolder, string path, EntryType type, string hash,
        BuildState state, params BuildArtifact[] artifacts) =>
        new()
        {
            Path = path, Exists = true, Hash = hash, Type = type,
            Build = new EntryBuild
            {
                Buildable = true, Source = path, Hash = hash, State = state,
                Root = Path.Combine(datasetFolder, ".ddb", "build", hash),
                Artifacts = artifacts
            }
        };

    /// <summary>
    /// A buildable entry whose build waits for <paramref name="missing"/> (a pending marker), awaiting them as
    /// libddb reports a marker that lists missing files; with none, a released marker that is due at once.
    /// </summary>
    public static EntryBuildArtifacts Pending(string datasetFolder, string path, EntryType type, string hash,
        params string[] missing)
    {
        var build = Build(datasetFolder, path, type, hash, BuildState.Pending);
        build.Build!.MissingDependencies = missing;
        build.Build.AwaitingFiles = missing.Length > 0;
        build.Build.LastAttempt = 1700000000;
        build.Build.RetryAfter = build.Build.LastAttempt + Registry.Adapters.Testing.PendingMarkers.LibddbRetryBackoffSeconds;
        return build;
    }

    /// <summary>A path the index does not have.</summary>
    public static EntryBuildArtifacts Missing(string path) => new() { Path = path, Exists = false };

    /// <summary>A file artifact.</summary>
    public static BuildArtifact File(string role, string path, string mediaType = "application/octet-stream",
        string? downloadName = null, bool legacy = false, string? fallbackFor = null, string? version = null) =>
        new()
        {
            Role = role, Path = path, Kind = "file", MediaType = mediaType, DownloadName = downloadName,
            Legacy = legacy, FallbackFor = fallbackFor, Version = version
        };

    /// <summary>A directory artifact read through its descriptor.</summary>
    public static BuildArtifact Directory(string role, string path, string index, bool legacy = false,
        string? fallbackFor = null, string? version = null, string? downloadName = null) =>
        new()
        {
            Role = role, Path = path, Kind = BuildArtifact.DirectoryKind, MediaType = "application/json", Index = index,
            DownloadName = downloadName, Legacy = legacy, FallbackFor = fallbackFor, Version = version
        };

    /// <summary>
    /// Answers <see cref="IDdbBuild.GetBuildArtifacts"/> of <paramref name="ddb"/> with <paramref name="builds"/>
    /// by path; a path without one is not buildable.
    /// </summary>
    public static Mock<IDDB> SetupBuilds(this Mock<IDDB> ddb, params EntryBuildArtifacts[] builds)
    {
        var byPath = builds.ToDictionary(b => b.Path, StringComparer.Ordinal);
        ddb.Setup(d => d.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>()))
            .Returns((IReadOnlyList<string> paths) =>
                paths.Select(p => byPath.TryGetValue(p, out var b) ? b : NotBuildable(p)).ToArray());
        return ddb;
    }
}
