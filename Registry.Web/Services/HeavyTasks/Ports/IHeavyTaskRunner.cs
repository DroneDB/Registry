using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.HeavyTasks.Models;

namespace Registry.Web.Services.HeavyTasks.Ports;

/// <summary>
/// Front door of the task substrate. Validates, plans, checks quota,
/// deduplicates and enqueues a heavy task onto the Hangfire <c>tasks</c> queue.
/// Authorization is performed by the caller (controller) before invocation.
/// </summary>
public interface IHeavyTaskRunner
{
    /// <summary>
    /// Runs the submit checks (<see cref="CheckSubmitAsync"/>) and enqueues a new task.
    /// </summary>
    /// <returns>The new task, or the equivalent task already there (deduplicated).</returns>
    Task<HeavyTaskSubmitResult> SubmitAsync(HeavyTaskSubmitRequest request, CancellationToken ct = default);

    /// <summary>
    /// The submit checks of every tool, in one place (FR-XC-25, D-013), then <paramref name="enqueue"/>: an output that
    /// is one of the inputs (400), the tool's own validation, then under the dataset's submit lock the deduplication
    /// (an equivalent task returns its id), an output that an unfinished task of the dataset writes (409) and the
    /// quota. Submit enqueues a new task; the retry of a failed task (D-037) passes its own requeue and its job id.
    /// </summary>
    /// <param name="request">The submit, as the caller resolved it.</param>
    /// <param name="enqueue">Starts the task once every check passed; returns its id. Runs under the submit lock.</param>
    /// <param name="ownJobId">The task being re-run (a retry): its own record is neither a duplicate nor a writer of its outputs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The started task, or the equivalent task already there (deduplicated).</returns>
    Task<HeavyTaskSubmitResult> CheckSubmitAsync(HeavyTaskSubmitRequest request,
        Func<HeavyTaskAdmission, CancellationToken, Task<string>> enqueue, string? ownJobId = null,
        CancellationToken ct = default);
}

/// <summary>A submit that passed the checks: what the enqueue stores in the task record.</summary>
/// <param name="Tool">The resolved tool.</param>
/// <param name="Request">The tool request.</param>
/// <param name="RequestHash">The deduplication hash (<c>HeavyTaskRequestHash</c>).</param>
/// <param name="OutputPaths">The files the task writes (<see cref="IHeavyTool.OutputPaths"/>).</param>
/// <param name="Label">The label of the task's target files (<see cref="IHeavyTool.Label"/>).</param>
public sealed record HeavyTaskAdmission(
    IHeavyTool Tool,
    HeavyToolRequest Request,
    string RequestHash,
    IReadOnlyList<string> OutputPaths,
    string? Label);
