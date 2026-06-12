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
using Registry.Web.Data;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;
using Serilog;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// Hangfire entry point that executes a resolved <see cref="IHeavyTool"/> on the
/// <c>tasks</c> queue (spec §4.10). Creates the per-task work directory, bridges
/// progress/log to <c>JobIndex</c>, persists artifact/error metadata and schedules
/// TTL cleanup of the work directory.
/// </summary>
public sealed class HeavyTaskJobWrapper
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHeavyToolRegistry _registry;
    private readonly ProcessingPlatformSettings _settings;
    private readonly string _tempPath;
    private readonly ILogger<HeavyTaskJobWrapper> _log;

    public HeavyTaskJobWrapper(
        IServiceScopeFactory scopeFactory,
        IHeavyToolRegistry registry,
        IOptions<AppSettings> appSettings,
        ILogger<HeavyTaskJobWrapper> log)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
        _settings = appSettings.Value.ProcessingPlatform ?? new ProcessingPlatformSettings();
        _tempPath = appSettings.Value.TempPath ?? Path.Combine(Path.GetTempPath(), "registry");
        _log = log;
    }

    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    [Queue("tasks")]
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
            workDir = Path.Combine(_tempPath, "tasks", taskId);
            Directory.CreateDirectory(workDir);
        }

        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<RegistryContext>();
        var ddbManager = sp.GetRequiredService<IDdbManager>();
        var indexWriter = sp.GetRequiredService<IJobIndexWriter>();
        var processor = sp.GetRequiredService<IBackgroundJobsProcessor>();

        var ds = await db.Datasets.AsNoTracking().Include(d => d.Organization)
                     .FirstOrDefaultAsync(d => d.Slug == request.DsSlug && d.Organization.Slug == request.OrgSlug,
                         cancellationToken)
                 ?? throw new InvalidOperationException(
                     $"Dataset '{request.OrgSlug}/{request.DsSlug}' not found for task {taskId}.");
        var ddb = ddbManager.Get(request.OrgSlug, ds.InternalRef);

        var execCtx = new HeavyToolExecutionContext(ddb, null, _log, taskId, workDir, ctx);
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
                    processor.Schedule(
                        () => DeleteWorkDirJob(workDir),
                        TimeSpan.FromHours(Math.Max(1, _settings.ArtifactTtlHours)));
                }
            }
        }
        catch (OperationCanceledException)
        {
            progress.FlushFinal();
            if (workDir is not null) SafeDeleteDir(workDir);
            throw;
        }
        catch (Exception ex)
        {
            progress.FlushFinal();
            try { await indexWriter.UpdateErrorAsync(taskId, ex.GetType().Name, cancellationToken); }
            catch { /* best effort */ }
            if (workDir is not null) SafeDeleteDir(workDir);
            throw;
        }
    }

    /// <summary>Hangfire-scheduled TTL cleanup of a finished task's work directory.</summary>
    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public static void DeleteWorkDirJob(string workDir) => SafeDeleteDir(workDir);

    private static void SafeDeleteDir(string dir)
    {
        try
        {
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete task work directory '{Dir}'", dir);
        }
    }
}
