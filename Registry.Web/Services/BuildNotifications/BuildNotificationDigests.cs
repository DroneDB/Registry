using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.BuildNotifications;

/// <summary>When the digest of a user runs: <see cref="BuildNotificationOptions.DigestMinutes"/> after their first waiting line.</summary>
public sealed class BuildNotificationDigests
{
    private readonly IBackgroundJobsProcessor _jobs;
    private readonly IOptions<BuildNotificationOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<BuildNotificationDigests> _logger;

    public BuildNotificationDigests(IBackgroundJobsProcessor jobs, IOptions<BuildNotificationOptions> options,
        TimeProvider time, ILogger<BuildNotificationDigests> logger)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Schedules the digest of <paramref name="userId"/> for the end of the window that opened at <paramref name="firstLineUtc"/> (now when it is over).</summary>
    /// <returns>The digest job.</returns>
    public string Schedule(string userId, DateTime firstLineUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);

        var due = firstLineUtc + TimeSpan.FromMinutes(_options.Value.DigestMinutes);
        var delay = due - _time.GetUtcNow().UtcDateTime;
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        var jobId = _jobs.Schedule<BuildNotificationDigestJob>(job => job.SendAsync(userId, CancellationToken.None), delay);
        _logger.LogDebug("Build notification digest of user {UserId} scheduled in {Delay} (job {JobId})", userId, delay, jobId);
        return jobId;
    }
}
