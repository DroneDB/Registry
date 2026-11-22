using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Builds;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// What every rescan task does, whichever entries it selects: rescan the index, log the summary, rebuild what the
/// rescan changed (<see cref="RescanRebuild"/>) and report progress. The tools (<see cref="RescanIndexTool"/>,
/// <see cref="RescanEntriesTool"/>) only validate and describe their request, and share the one parameter they have
/// in common, <c>stopOnError</c>.
/// <see cref="RescanRebuild"/> is resolved through <see cref="IServiceScopeFactory"/>: it needs the
/// <see cref="IDatasetCacheInvalidator"/>, registered on every host (including processing nodes) unlike the full
/// <c>IObjectsManager</c>.
/// </summary>
public sealed class RescanExecution(IServiceScopeFactory scopeFactory)
{
    /// <summary>The name of the parameter that stops the rescan at the first entry error.</summary>
    public const string StopOnErrorParam = "stopOnError";

    /// <summary>The JSON schema property of <see cref="StopOnErrorParam"/>, for the input schema of a rescan tool.</summary>
    public const string StopOnErrorSchemaProperty =
        """
        "stopOnError": { "type": ["boolean", "null"], "default": false, "description": "Stop on the first entry error." }
        """;

    /// <summary>
    /// The request of a rescan names its entries in the parameters of the tool that runs it: a path of the request
    /// itself would be ignored, so it is refused.
    /// </summary>
    /// <exception cref="BadRequestException">The request has a path.</exception>
    public static void RequireNoRequestPath(HeavyToolRequest request, string hint)
    {
        if (!string.IsNullOrEmpty(request.Path))
            throw new BadRequestException($"The tool '{request.ToolId}' takes no path. {hint}");
    }

    /// <summary>Rescans the entries <paramref name="filter"/> selects, then rebuilds them (or the dataset, for no filter).</summary>
    public async Task RunAsync(HeavyToolRequest request, IHeavyToolExecutionContext ctx, RescanFilter filter,
        bool stopOnError, IProgress<HeavyToolProgress> progress, CancellationToken ct)
    {
        progress.Report(new HeavyToolProgress(-1, "rescanning",
            LogChunk: $"Rescanning index ({filter})"));
        var results = ctx.Ddb.RescanIndex(filter.ToOptions(stopOnError));

        ct.ThrowIfCancellationRequested();

        var ok = results.Count(r => r.Success);
        var summary = $"{ok}/{results.Count} ok, {results.Count - ok} errors";
        progress.Report(new HeavyToolProgress(-1, "rebuilding", LogChunk: $"Rescan: {summary}. Rebuilding derivatives"));

        using var scope = scopeFactory.CreateScope();
        var rebuild = await scope.ServiceProvider.GetRequiredService<RescanRebuild>().RunAsync(
            new BuildTarget(ctx.Ddb, ctx.Dataset, request.OrgSlug, request.DsSlug), filter, results, new InlineBuilds(ctx),
            f => progress.Report(new HeavyToolProgress(f, "rebuilding")), ct: ct);

        progress.Report(new HeavyToolProgress(1, "done", LogChunk: rebuild.WholeDataset
            ? $"Rescan complete: {summary}"
            : $"Rescan complete: {summary}; rebuilt {rebuild.Built}, {rebuild.Failed} rebuild failures"));
    }

    // Inline, one build at a time (same approach as BuildTool): this task already holds a
    // builds-queue worker, so a rescan across many datasets cannot flood the build queue.
    private sealed class InlineBuilds(IHeavyToolExecutionContext ctx) : IRescanBuildRunner
    {
        public Task BuildDataset()
        {
            ctx.Builds.BuildNow(ctx.Ddb, null, true, ctx.Hangfire);
            return Task.CompletedTask;
        }

        public Task BuildEntry(RescanResult entry)
        {
            ctx.Builds.BuildNow(ctx.Ddb, entry.Path, true, ctx.Hangfire);
            return Task.CompletedTask;
        }

        public Task BuildPending()
        {
            ctx.Builds.BuildPendingNow(ctx.Ddb, ctx.Hangfire);
            return Task.CompletedTask;
        }
    }
}
