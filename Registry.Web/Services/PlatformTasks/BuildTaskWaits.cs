using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.PlatformTasks;

/// <summary>
/// The tasks of a list whose entry waits for files, with the files each one waits for (dataset-relative, as libddb
/// names them). A task that is not in it does not wait.
/// </summary>
public sealed class TasksWaitingForFiles
{
    /// <summary>No task waits.</summary>
    public static readonly TasksWaitingForFiles None = new(new Dictionary<string, IReadOnlyList<string>>());

    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _missingByTaskId;

    public TasksWaitingForFiles(IReadOnlyDictionary<string, IReadOnlyList<string>> missingByTaskId)
    {
        _missingByTaskId = missingByTaskId ?? throw new ArgumentNullException(nameof(missingByTaskId));
    }

    /// <summary>The files the task <paramref name="taskId"/> waits for; null when it does not wait.</summary>
    public IReadOnlyList<string>? MissingFilesOf(string taskId) =>
        _missingByTaskId.TryGetValue(taskId, out var missing) ? missing : null;
}

/// <summary>
/// Which succeeded build tasks have their entry waiting for files now. libddb defers the build of content that names
/// files the dataset lacks (the .mtl of an .obj) with a normal return, so the build job succeeds; the entry keeps the
/// pending marker of that content, which waits for the files. The answer is read from libddb when a list is made, with
/// the rule of the build e-mails (<see cref="IBuildOutcomeClassifier"/>): nothing is kept with the task. Only the
/// succeeded builds of an entry are looked up, with one native call for their entries and at most one for the markers
/// per dataset; a dataset whose index libddb cannot read, or whose folder is gone, leaves its tasks as they are (a list
/// does not fail for it).
/// </summary>
public sealed class BuildTaskWaits
{
    private readonly IBuildOutcomeClassifier _classifier;
    private readonly IDdbManager _ddbManager;
    private readonly RegistryContext _context;
    private readonly ILogger<BuildTaskWaits> _logger;

    public BuildTaskWaits(IBuildOutcomeClassifier classifier, IDdbManager ddbManager, RegistryContext context,
        ILogger<BuildTaskWaits> logger)
    {
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Whether <paramref name="task"/> can wait for files: the build of an entry that succeeded.</summary>
    public static bool CanWait(JobIndex task) =>
        task.ToolId == HeavyToolIds.Build && task.CurrentState == SucceededState.StateName &&
        !string.IsNullOrEmpty(task.Path);

    /// <summary>
    /// The tasks of <paramref name="tasks"/>, all of one dataset, that wait for files. The index is opened
    /// (<paramref name="openDdb"/>) only when one of them can wait.
    /// </summary>
    public TasksWaitingForFiles Find(IReadOnlyCollection<JobIndex> tasks, Func<IDDB> openDdb)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(openDdb);

        var missing = new Dictionary<string, IReadOnlyList<string>>();
        AddWaiting(tasks.Where(CanWait).ToArray(), openDdb, missing);
        return missing.Count == 0 ? TasksWaitingForFiles.None : new TasksWaitingForFiles(missing);
    }

    /// <summary>
    /// The tasks of <paramref name="tasks"/>, of any datasets, that wait for files: one query for the datasets of the
    /// tasks that can wait, and each of their indexes opened once.
    /// </summary>
    public async Task<TasksWaitingForFiles> FindAsync(IReadOnlyCollection<JobIndex> tasks,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var byDataset = tasks.Where(CanWait).GroupBy(t => (t.OrgSlug, t.DsSlug)).ToArray();
        if (byDataset.Length == 0)
            return TasksWaitingForFiles.None;

        string[] orgSlugs = [.. byDataset.Select(g => g.Key.OrgSlug).Distinct()];
        string[] dsSlugs = [.. byDataset.Select(g => g.Key.DsSlug).Distinct()];
        var datasets = await _context.Datasets.AsNoTracking()
            .Where(d => orgSlugs.Contains(d.Organization.Slug) && dsSlugs.Contains(d.Slug))
            .Select(d => new { OrgSlug = d.Organization.Slug, DsSlug = d.Slug, d.InternalRef })
            .ToArrayAsync(ct);
        var internalRefOf = datasets.ToDictionary(d => (d.OrgSlug, d.DsSlug), d => d.InternalRef);

        var missing = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var dataset in byDataset)
        {
            // Deleted since: no entry waits
            if (!internalRefOf.TryGetValue(dataset.Key, out var internalRef))
                continue;

            AddWaiting([.. dataset], () => _ddbManager.Open(dataset.Key.OrgSlug, internalRef), missing);
        }

        return missing.Count == 0 ? TasksWaitingForFiles.None : new TasksWaitingForFiles(missing);
    }

    // The tasks (of one dataset, all of which can wait) whose entry waits, added to missing
    private void AddWaiting(JobIndex[] tasks, Func<IDDB> openDdb, Dictionary<string, IReadOnlyList<string>> missing)
    {
        if (tasks.Length == 0)
            return;

        try
        {
            var outcomes = _classifier.ClassifyAll(openDdb(),
                [.. tasks.Select(t => new EndedBuildJob(t.Path!, t.Hash, Succeeded: true))]);

            for (var i = 0; i < tasks.Length; i++)
                if (outcomes[i] is { Outcome: BuildOutcome.WaitingForFiles } waiting)
                    missing[tasks[i].JobId] = waiting.MissingFiles;
        }
        // libddb cannot read the index, or the dataset's folder is gone
        catch (DdbException ex)
        {
            NotRead(ex, tasks[0]);
        }
        catch (System.IO.IOException ex)
        {
            NotRead(ex, tasks[0]);
        }
    }

    private void NotRead(Exception ex, JobIndex task) =>
        _logger.LogWarning(ex, "Cannot tell which build tasks of {Org}/{Ds} wait for files", task.OrgSlug,
            task.DsSlug);
}
