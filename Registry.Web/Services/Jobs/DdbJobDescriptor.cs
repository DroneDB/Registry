#nullable enable
using Hangfire.Common;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Jobs;

/// <summary>What a dataset job does.</summary>
public enum DdbJobKind
{
    Build,
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
        if (job?.Method == null)
            return null;

        if (job.Type == typeof(DdbJobs))
        {
            // (orgSlug, internalRef, path, ...)
            return job.Method.Name switch
            {
                nameof(DdbJobs.Build) => new DdbJobDescriptor(DdbJobKind.Build, Arg(job, 2)),
                nameof(DdbJobs.BuildPending) => new DdbJobDescriptor(DdbJobKind.BuildPending, null),
                nameof(DdbJobs.Cleanup) => new DdbJobDescriptor(DdbJobKind.Cleanup, null),
                nameof(DdbJobs.MaskBorders) => new DdbJobDescriptor(DdbJobKind.MaskBorders, Arg(job, 2)),
                nameof(DdbJobs.GenerateThumbnail) => new DdbJobDescriptor(DdbJobKind.GenerateThumbnail, Arg(job, 2)),
                _ => null
            };
        }

        if (job.Type == typeof(HangfireUtils))
        {
            // (IDDB ddb, path, ...)
            return job.Method.Name switch
            {
                nameof(HangfireUtils.BuildWrapper) => new DdbJobDescriptor(DdbJobKind.Build, Arg(job, 1)),
                nameof(HangfireUtils.BuildPendingWrapper) => new DdbJobDescriptor(DdbJobKind.BuildPending, null),
                nameof(HangfireUtils.CleanupWrapper) => new DdbJobDescriptor(DdbJobKind.Cleanup, null),
                nameof(HangfireUtils.MaskBordersWrapper) => new DdbJobDescriptor(DdbJobKind.MaskBorders, Arg(job, 1)),
                nameof(HangfireUtils.GenerateThumbnailWrapper) =>
                    new DdbJobDescriptor(DdbJobKind.GenerateThumbnail, Arg(job, 1)),
                _ => null
            };
        }

        return null;
    }

    /// <summary>Whether the job builds (an entry, the whole dataset or the pending entries).</summary>
    public bool IsBuild => Kind is DdbJobKind.Build or DdbJobKind.BuildPending;

    /// <summary>Whether the job runs a native build inline and so belongs on the builds queue.</summary>
    public bool RunsNativeBuild => Kind is DdbJobKind.Build or DdbJobKind.MaskBorders;

    private static string? Arg(Job job, int index) => job.Args.Count > index ? job.Args[index] as string : null;
}
