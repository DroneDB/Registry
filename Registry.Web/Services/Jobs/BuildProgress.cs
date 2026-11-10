using System;
using Hangfire.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports.DroneDB;
using Registry.Web.Filters;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Models;

namespace Registry.Web.Services.Jobs;

/// <summary>
/// The progress of a build as libddb reports it (<c>DDBBuildEx2</c>, FR-PRC-42), as the
/// <see cref="HeavyToolProgress"/> of its task: the fraction (-1 while nothing is measured: no percent) and the phase libddb
/// names (the entry type, then "warping", "overviews" and "compressing" for a raster, "publishing"), with a log line at each
/// new phase. The last report of a build that ends without an error (fraction 1, no phase) is "done".
/// </summary>
public static class BuildProgress
{
    /// <summary>The phase of the last report of a completed build.</summary>
    public const string Done = "done";

    /// <summary>
    /// The observer to hand to <see cref="IDdbBuild.BuildWithProgress"/>: each report of libddb is reported to
    /// <paramref name="progress"/>. libddb makes one call at a time (from the calling thread or a worker of the build).
    /// </summary>
    public static Action<double, string?> To(IProgress<HeavyToolProgress> progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        string? lastPhase = null;
        return (fraction, phase) =>
        {
            var named = phase ?? (fraction >= 1 ? Done : null);
            string? logChunk = null;
            if (named is not null && named != lastPhase)
            {
                lastPhase = named;
                logChunk = fraction < 0 ? named : $"{named} ({(int)Math.Round(Math.Clamp(fraction, 0, 1) * 100)}%)";
            }

            progress.Report(new HeavyToolProgress(fraction, named, LogChunk: logChunk));
        };
    }
}

/// <summary>
/// The progress of a build job (<see cref="DdbJobs.Build"/>, <see cref="DdbJobs.RebuildInBackground"/>) in its
/// <c>JobIndex</c> row, as a heavy task writes its own (<see cref="HangfireProgressSink"/>: the percent and the phase,
/// throttled, and the phase lines in the job's console and log tail): a build task of the Hub shows its percent
/// (FR-PRC-42).
/// </summary>
public sealed class BuildJobProgress
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ProcessingPlatformSettings _settings;
    private readonly ILogger<HangfireProgressSink> _log;

    public BuildJobProgress(IServiceScopeFactory scopes,
        IOptions<ProcessingPlatformSettings> settings, ILogger<HangfireProgressSink> log)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// The sink of the job of <paramref name="context"/>, or null when there is no job (a call outside Hangfire). Complete it (<see cref="HangfireProgressSink.CompleteAsync"/>)
    /// when the build returns. Its log lines go to the log tail <see cref="BuildLogCaptureFilter"/> keeps for the job.
    /// </summary>
    public HangfireProgressSink? Open(PerformContext? context)
    {
        var jobId = context?.BackgroundJob?.Id;
        if (string.IsNullOrEmpty(jobId))
            return null;

        var tail = context!.Items.TryGetValue(BuildLogCaptureFilter.BufferKey, out var raw) && raw is LogRingBuffer buffer
            ? buffer
            : new LogRingBuffer(_settings.LogTailMaxLines, _settings.LogTailMaxBytes);
        return new HangfireProgressSink(context, _scopes, tail, jobId, _settings.ProgressUpdateThrottleSeconds, _log);
    }
}
