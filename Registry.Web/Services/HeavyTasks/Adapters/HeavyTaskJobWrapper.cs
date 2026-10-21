#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Serilog;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// Hangfire entry point that executes a resolved <see cref="IHeavyTool"/> on the
/// <c>tasks</c> queue. Creates the per-task work directory, bridges
/// progress/log to <c>JobIndex</c>, persists artifact/error metadata and schedules
/// TTL cleanup of the work directory.
/// </summary>
public sealed class HeavyTaskJobWrapper : DdbRetriedJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHeavyToolRegistry _registry;
    private readonly ProcessingPlatformSettings _settings;
    private readonly string _tempPath;
    private readonly IFileSystem _fs;
    private readonly FileJobs _files;
    private readonly ILogger<HeavyTaskJobWrapper> _log;

    public HeavyTaskJobWrapper(
        IServiceScopeFactory scopeFactory,
        IHeavyToolRegistry registry,
        IOptions<ProcessingPlatformSettings> platformSettings,
        IScratchFileProvider scratch,
        IFileSystem fs,
        FileJobs files,
        ILogger<HeavyTaskJobWrapper> log)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
        _settings = platformSettings.Value;
        _tempPath = scratch.Root;
        _fs = fs;
        _files = files;
        _log = log;
    }

    // The retry policy is the class's (DdbRetriedJob): transient DroneDB contention gets a backoff
    // chain, anything else fails fast.
    // No [Queue]: BuildQueueRoutingFilter routes this job to "tasks" or "builds" by tool.
    public async Task Run(string toolId, string toolVersion, string requestJson,
        PerformContext ctx, CancellationToken cancellationToken)
    {
        var taskId = ctx.BackgroundJob.Id;

        var tool = _registry.Resolve(toolId, toolVersion)
                   ?? throw new InvalidOperationException($"Tool '{toolId}' v'{toolVersion}' is not registered.");

        var request = JsonSerializer.Deserialize<HeavyToolRequest>(requestJson)
                      ?? throw new InvalidOperationException("Invalid heavy task request payload.");

        string? workDir = null;
        if (tool.ProducesArtifact)
        {
            workDir = TaskJobHelpers.ArtifactDirectory(taskId, _tempPath)
                      ?? throw new InvalidOperationException($"Invalid task id '{taskId}'.");
            _fs.FolderCreate(workDir);
        }

        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<RegistryContext>();
        var ddbManager = sp.GetRequiredService<IDdbManager>();
        var indexWriter = sp.GetRequiredService<IJobIndexWriter>();
        var processor = sp.GetRequiredService<IBackgroundJobsProcessor>();
        var builds = sp.GetRequiredService<DdbJobs>();

        var ds = await db.Datasets.AsNoTracking().Include(d => d.Organization)
                     .FirstOrDefaultAsync(d => d.Slug == request.DsSlug && d.Organization.Slug == request.OrgSlug,
                         cancellationToken)
                 ?? throw new InvalidOperationException(
                     $"Dataset '{request.OrgSlug}/{request.DsSlug}' not found for task {taskId}.");
        var ddb = ddbManager.Get(request.OrgSlug, ds.InternalRef);

        var execCtx = new HeavyToolExecutionContext(ddb, new DatasetKey(request.OrgSlug, ds.InternalRef), null, _log,
            taskId, workDir, ctx, processor, builds);
        var logBuffer = new LogRingBuffer(_settings.LogTailMaxLines, _settings.LogTailMaxBytes);
        var progress = new HangfireProgressSink(ctx, indexWriter, logBuffer, taskId,
            _settings.ProgressUpdateThrottleSeconds);

        try
        {
            await tool.ValidateAsync(request, execCtx, cancellationToken);
            var artifact = await tool.ExecuteAsync(request, execCtx, progress, cancellationToken);
            progress.FlushFinal();

            if (artifact is not null)
            {
                await indexWriter.UpdateArtifactAsync(taskId, artifact.SizeBytes, artifact.Sha256, cancellationToken);

                if (workDir is not null)
                {
                    processor.Schedule<FileJobs>(
                        j => j.Delete(workDir, null),
                        TimeSpan.FromHours(Math.Max(1, _settings.ArtifactTtlHours)));
                }
            }
        }
        catch (OperationCanceledException)
        {
            progress.FlushFinal();
            DeleteWorkDir(workDir);
            throw;
        }
        catch (Exception ex)
        {
            progress.FlushFinal();
            try { await indexWriter.UpdateErrorAsync(taskId, ex.GetType().Name, cancellationToken); }
            catch { /* best effort */ }
            DeleteWorkDir(workDir);
            throw;
        }
    }

    /// <summary>
    /// Hangfire-scheduled TTL cleanup of a finished task's work directory, kept for the jobs
    /// scheduled before RQ-10 (D4): new schedules use <see cref="FileJobs.Delete"/>.
    /// </summary>
    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public void DeleteWorkDirJob(string workDir) => _files.Delete(workDir, null);

    private void DeleteWorkDir(string? workDir) =>
        _fs.TryDelete(workDir, ex => _log.LogWarning(ex, "Failed to delete task work directory '{Dir}'", workDir));
}
