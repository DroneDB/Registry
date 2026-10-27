#nullable enable
using System;
using Hangfire.States;
using Microsoft.Extensions.Logging;

namespace Registry.Web.Filters;

/// <summary>
/// The one guard of the work the state filters do when a job changes state: a failure of that work (the database, the
/// cache or libddb unavailable) is logged and the transition goes on. Thrown out of a filter it would fail the state
/// change itself, and Hangfire would run the job again.
/// </summary>
internal static class StateFilterGuard
{
    /// <param name="log">The filter's logger.</param>
    /// <param name="context">The state change.</param>
    /// <param name="work">What the filter does, e.g. "recording the failure".</param>
    /// <param name="action">The work.</param>
    public static void Run(ILogger log, ApplyStateContext context, string work, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Job {JobId} entering {State}: {Work} failed; the state change goes on",
                context.BackgroundJob?.Id, context.NewState?.Name, work);
        }
    }
}
