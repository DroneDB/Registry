#nullable enable
using System;
using Hangfire.Common;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Jobs;

/// <summary>What a dataset job does.</summary>
public enum DdbJobKind
{
    Build,
    RebuildInBackground,
    BuildPending,
    Cleanup,
    MaskBorders,
    GenerateThumbnail
}

/// <summary>
/// The kind and entry path of a dataset job, for the filters that act on build jobs. It knows both
/// shapes: <see cref="DdbJobs"/> and the jobs stored by earlier releases (<see cref="HangfireUtils"/>),
/// so a filter never matches method names or argument positions itself.
/// </summary>
/// <param name="Kind">What the job does.</param>
/// <param name="Path">The entry it works on (build, mask borders, thumbnail), or null.</param>
public readonly record struct DdbJobDescriptor(DdbJobKind Kind, string? Path)
{
    /// <summary>The descriptor of <paramref name="job"/>, or null when it is not a dataset job.</summary>
    public static DdbJobDescriptor? Of(Job? job)
    {
        if (job?.Method == null || KindOf(job.Type, job.Method.Name) is not { } kind)
            return null;

        // DdbJobs: (orgSlug, internalRef, path, ...); the jobs of earlier releases: (IDDB ddb, path, ...)
        return new DdbJobDescriptor(kind, HasPath(kind) ? Arg(job, job.Type == typeof(DdbJobs) ? 2 : 1) : null);
    }

    /// <summary>
    /// The descriptor of the job named by the <paramref name="methodDisplay"/> of its job index row (Hangfire's
    /// <c>Type.Method</c> text of the job), without a path (the row has its own), or null when the text names no
    /// dataset job: the row of another tool, or one that stored no text. It knows the same jobs as <see cref="Of"/>,
    /// whichever shape the job was stored in.
    /// </summary>
    public static DdbJobDescriptor? OfDisplay(string? methodDisplay)
    {
        var dot = methodDisplay?.LastIndexOf('.') ?? -1;
        if (dot < 0)
            return null;

        var type = methodDisplay![..dot] switch
        {
            nameof(DdbJobs) => typeof(DdbJobs),
            nameof(HangfireUtils) => typeof(HangfireUtils),
            _ => null
        };
        return type != null && KindOf(type, methodDisplay[(dot + 1)..]) is { } kind
            ? new DdbJobDescriptor(kind, null)
            : null;
    }

    /// <summary>Whether <paramref name="methodDisplay"/> (see <see cref="OfDisplay"/>) names a background rebuild.</summary>
    public static bool IsBackgroundRebuildDisplay(string? methodDisplay) =>
        OfDisplay(methodDisplay) is { IsBackgroundRebuild: true };

    private static DdbJobKind? KindOf(Type type, string method)
    {
        if (type == typeof(DdbJobs))
        {
            return method switch
            {
                nameof(DdbJobs.Build) => DdbJobKind.Build,
                nameof(DdbJobs.RebuildInBackground) => DdbJobKind.RebuildInBackground,
                nameof(DdbJobs.BuildPending) => DdbJobKind.BuildPending,
                nameof(DdbJobs.Cleanup) => DdbJobKind.Cleanup,
                nameof(DdbJobs.MaskBorders) => DdbJobKind.MaskBorders,
                nameof(DdbJobs.GenerateThumbnail) => DdbJobKind.GenerateThumbnail,
                _ => null
            };
        }

        if (type == typeof(HangfireUtils))
        {
            return method switch
            {
                nameof(HangfireUtils.BuildWrapper) => DdbJobKind.Build,
                nameof(HangfireUtils.BuildPendingWrapper) => DdbJobKind.BuildPending,
                nameof(HangfireUtils.CleanupWrapper) => DdbJobKind.Cleanup,
                nameof(HangfireUtils.MaskBordersWrapper) => DdbJobKind.MaskBorders,
                nameof(HangfireUtils.GenerateThumbnailWrapper) => DdbJobKind.GenerateThumbnail,
                _ => null
            };
        }

        return null;
    }

    // The jobs of one entry; the pending-build job and the cleanup work on the whole dataset
    private static bool HasPath(DdbJobKind kind) => kind is not (DdbJobKind.BuildPending or DdbJobKind.Cleanup);

    /// <summary>Whether the job builds (an entry, the whole dataset or the pending entries).</summary>
    public bool IsBuild => Kind is DdbJobKind.Build or DdbJobKind.RebuildInBackground or DdbJobKind.BuildPending;

    /// <summary>
    /// Whether the job builds the entry at <see cref="Path"/> itself (a build, a background rebuild): when it fails
    /// with an error libddb retries, the entry keeps its pending marker and the pending-build coordinator retries it.
    /// </summary>
    public bool BuildsEntry => Kind is DdbJobKind.Build or DdbJobKind.RebuildInBackground;

    /// <summary>Whether the job is the low-priority build of an entry of an older layout (the rebuilds queue).</summary>
    public bool IsBackgroundRebuild => Kind is DdbJobKind.RebuildInBackground;

    /// <summary>
    /// Whether the job runs a native build inline and so belongs on the builds queue (the background rebuild builds
    /// too, but on its own queue, <see cref="IsBackgroundRebuild"/>).
    /// </summary>
    public bool RunsNativeBuild => Kind is DdbJobKind.Build or DdbJobKind.MaskBorders;

    private static string? Arg(Job job, int index) => job.Args.Count > index ? job.Args[index] as string : null;
}
