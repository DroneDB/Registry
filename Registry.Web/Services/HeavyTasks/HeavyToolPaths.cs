using System;
using System.Collections.Generic;
using System.Linq;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;

namespace Registry.Web.Services.HeavyTasks;

/// <summary>What a heavy-tool request reads and writes, and the label of its target (FR-XC-25).</summary>
/// <param name="Inputs">The files the task reads.</param>
/// <param name="Outputs">The files the task writes.</param>
/// <param name="Label">The label of the task's target files, or null.</param>
public sealed record DeclaredToolPaths(IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs, string? Label);

/// <summary>
/// The path rules every tool output follows, in one place (FR-XC-25, FR-XC-07, D-040): an output may not be one of
/// the task's inputs (400), and two paths name the same file when they match without regard to letter case.
/// </summary>
public static class HeavyToolPaths
{
    /// <summary>How the paths of a dataset compare (D-040: names in a folder clash without regard to case).</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>The message of an output that is one of the inputs.</summary>
    /// <returns>The 400 detail.</returns>
    public static string OutputIsInputMessage(string path) =>
        $"The output '{path}' is also one of the input files: choose another output path.";

    /// <summary>The message of an output that an active task of the dataset already writes.</summary>
    /// <returns>The 409 detail.</returns>
    public static string OutputInUseMessage(string path) =>
        $"Another task of this dataset that has not finished writes '{path}': wait for it to end, or choose another output path.";

    /// <summary>
    /// What <paramref name="tool"/> declares for <paramref name="request"/>.
    /// </summary>
    /// <returns>The inputs, outputs and label.</returns>
    public static DeclaredToolPaths Declared(IHeavyTool tool, HeavyToolRequest request)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(request);
        return new DeclaredToolPaths(tool.InputPaths(request), tool.OutputPaths(request), tool.Label(request));
    }

    /// <summary>
    /// What <paramref name="tool"/> declares for <paramref name="request"/>, or null when its parameters are not valid
    /// yet (the tool's own validation reports them).
    /// </summary>
    /// <returns>The declared paths, or null.</returns>
    public static DeclaredToolPaths? TryDeclared(IHeavyTool tool, HeavyToolRequest request)
    {
        try
        {
            return Declared(tool, request);
        }
        catch (Exception ex) when (ex is ArgumentException or BadRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Refuses an output that is one of the inputs (D-040: 400, never a name clash, since no overwrite can make it valid).
    /// </summary>
    /// <param name="inputs">The files the task reads.</param>
    /// <param name="outputs">The files the task writes.</param>
    /// <exception cref="BadRequestException">An output is one of the inputs.</exception>
    public static void EnsureOutputsAreNotInputs(IEnumerable<string> inputs, IEnumerable<string> outputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);

        var read = inputs.ToHashSet(Comparer);
        var clash = outputs.FirstOrDefault(read.Contains);
        if (clash != null)
            throw new BadRequestException(OutputIsInputMessage(clash), noRetry: true);
    }
}
