using System;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Models;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Jobs;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Background job behind the system rescan endpoint: finds the datasets of a scope that
/// hold entries matching a <see cref="RescanFilter"/> and submits one <see cref="HeavyToolIds.RescanIndex"/>
/// task for each of them. The tasks run on the builds queue, so the heavy worker count
/// bounds how many datasets are rescanned at once, and each task shows in its dataset's
/// task list. Datasets without a matching entry get no task.
/// </summary>
public class IndexRescanService
{
    private readonly RegistryContext _context;
    private readonly IDdbManager _ddbManager;
    private readonly ISystemTaskSubmitter _taskSubmitter;
    private readonly ILogger<IndexRescanService> _logger;

    public IndexRescanService(
        RegistryContext context,
        IDdbManager ddbManager,
        ISystemTaskSubmitter taskSubmitter,
        ILogger<IndexRescanService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _taskSubmitter = taskSubmitter ?? throw new ArgumentNullException(nameof(taskSubmitter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <param name="orgSlug">Organization of the scope, or null for all organizations.</param>
    /// <param name="dsSlug">Dataset of the scope, or null for all datasets of the scope.</param>
    /// <param name="types">Comma-separated entry types, or null for all.</param>
    /// <param name="extensions">Comma-separated file extensions, or null for all.</param>
    /// <param name="stopOnError">Passed to each dataset rescan.</param>
    /// <param name="userId">The admin who asked for the rescan; the tasks are attributed to them.</param>
    /// <param name="context">Hangfire context, injected at run time.</param>
    [AutomaticRetry(Attempts = 0)]
    public async Task SubmitAsync(string? orgSlug, string? dsSlug, string? types, string? extensions,
        bool stopOnError, string? userId, PerformContext? context = null)
    {
        var console = JobConsole.For(_logger, context);

        var scope = DatasetScope.Parse(orgSlug, dsSlug);
        var filter = RescanFilter.Parse(types, extensions).RequireNotEmpty();

        var datasets = await scope.Apply(_context.Datasets.AsNoTracking())
            .OrderBy(d => d.Organization.Slug).ThenBy(d => d.Slug)
            .Select(d => new { d.Slug, OrgSlug = d.Organization.Slug, d.InternalRef })
            .ToArrayAsync();

        console.Info("Rescanning entries ({Filter}) in {Datasets} dataset(s) of {Scope}", filter, datasets.Length, scope);

        var prms = RescanIndexTool.CreateParams(filter, stopOnError);

        int submitted = 0, matched = 0, failed = 0;
        foreach (var ds in datasets)
        {
            var key = $"{ds.OrgSlug}/{ds.Slug}";
            try
            {
                var ddb = _ddbManager.Get(ds.OrgSlug, ds.InternalRef);
                var count = ddb.FindSummaries(filter.ToEntryQuery()).Count(filter.Matches);
                if (count == 0)
                    continue;

                var taskId = await _taskSubmitter.SubmitAsync(HeavyToolIds.RescanIndex, ds.OrgSlug, ds.Slug, ddb, null, prms, userId);
                submitted++;
                matched += count;
                console.Info("{Dataset}: {Count} matching entries, task {TaskId}", key, count, taskId);
            }
            catch (Exception ex)
            {
                failed++;
                console.Warning(ex, "cannot submit the rescan of {Dataset}: {Error}", key, ex.Message);
            }
        }

        console.Info("Rescan submitted: {Submitted} task(s) for {Matched} matching entries, " +
                     "{WithoutMatches} dataset(s) without matches, {Failed} failure(s)",
            submitted, matched, datasets.Length - submitted - failed, failed);
    }
}
