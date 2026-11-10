using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Registry.Ports.DroneDB;
using Registry.Web.Models;

namespace Registry.Web.Services.Maintenance;

/// <summary>
/// One of the rescans a Registry runs on the first start of a release that changes what an index holds
/// (<see cref="Initialization.UpgradeRescanStartupTask"/>): it names the entry types whose entries the release
/// has to read again. The steps share one job and one rescan of each dataset, so a dataset is read once whatever the
/// number of steps: the job rescans the entries of every type named by a step not yet done for the dataset, and
/// rebuilds the entries whose build key changed (D-020). The progress is kept per step, so a step registered by a
/// later release runs for every dataset and the steps already done do not run again. A later release part adds its
/// rescan by registering one more step
/// (<c>services.TryAddEnumerable(ServiceDescriptor.Singleton&lt;IUpgradeRescanStep, ...&gt;())</c>): the vector footprints
/// of D-050 are one.
/// </summary>
public interface IUpgradeRescanStep
{
    /// <summary>
    /// The name of the step in the log of the job and in its state file: the job records the datasets done for each
    /// id, so it must not change once released.
    /// </summary>
    string Id { get; }

    /// <summary>The entry types the step has the job rescan.</summary>
    IReadOnlyList<EntryType> EntryTypes { get; }

    /// <summary>
    /// The least ABI minor (of the loaded libddb 2.x) whose rescan gives what the step is for: the job skips the step,
    /// recording nothing for it, until the library reports it, and runs it once at the first start with such a library.
    /// A rescan with a libddb older than 2.2 (no build key) is never run, so the default is the build key's minor.
    /// </summary>
    int RequiredAbiMinor => BuildKeyRescanStep.RequiredMinor;
}

/// <summary>
/// The build-key rescan (D-020): the models and the splats with a georeference move to the build folder of their
/// key (<see cref="EntryBuild.Key"/>). Their folders named by the hash alone, from before the build key, hold the build
/// of whichever copy of the file made it, so libddb reports these entries as changed and the job rebuilds them. The
/// entries of the other types keep their folders. The same rescan moves the pending markers to per-path markers.
/// </summary>
public sealed class BuildKeyRescanStep : IUpgradeRescanStep
{
    /// <summary>The id of the step, also the one a state file of the first shape (no step ids) recorded.</summary>
    public const string StepId = "build-key";

    /// <summary>The ABI minor of libddb 2.x that has the build key (DroneDB 2.2.0, U-210).</summary>
    public const int RequiredMinor = 2;

    public string Id => StepId;

    public int RequiredAbiMinor => RequiredMinor;

    public IReadOnlyList<EntryType> EntryTypes { get; } = [EntryType.Model, EntryType.GaussianSplat];
}

/// <summary>
/// The vector footprint rescan (FR-SHR-03, D-050): libddb 2.3 keeps the footprint of a vector entry in WGS 84, or none
/// when it cannot tell the coordinates (it kept the raw envelope, in the metres of a UTM file, before). An index written
/// by an earlier release is corrected by reading the vector entries again. The rescan goes with the one of the build key
/// (one job, one read of each dataset): a vector entry has no build whose key changes, so nothing is rebuilt for it.
/// libddb 2.2 does not have the fix, so a rescan with it would correct nothing: the job skips the step, records nothing
/// for it (<see cref="RequiredAbiMinor"/>), and runs it once at the first start with libddb 2.3.
/// </summary>
public sealed class VectorFootprintRescanStep : IUpgradeRescanStep
{
    /// <summary>The id of the step in the log and the state file of the job: it must not change once released.</summary>
    public const string StepId = "vector-footprints";

    /// <summary>The ABI minor of libddb 2.x whose vector footprints are WGS 84 (FR-SHR-03).</summary>
    public const int WgsFootprintAbiMinor = 3;

    public string Id => StepId;

    public int RequiredAbiMinor => WgsFootprintAbiMinor;

    public IReadOnlyList<EntryType> EntryTypes { get; } = [EntryType.Vector];
}

/// <summary>What the registered steps have the job rescan.</summary>
public static class UpgradeRescanSteps
{
    /// <summary>
    /// The filter of the rescan of a dataset: the entry types of every step, each once.
    /// </summary>
    /// <exception cref="InvalidOperationException">No step names an entry type.</exception>
    public static RescanFilter FilterOf(IEnumerable<IUpgradeRescanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var types = steps.SelectMany(s => s.EntryTypes).Distinct().ToArray();
        if (types.Length == 0)
            throw new InvalidOperationException("No upgrade rescan step names an entry type");

        return RescanFilter.Parse(types.Select(t => t.ToString()), null);
    }
}

/// <summary>
/// What the upgrade rescans have done, in <see cref="MagicStrings.UpgradeRescansFileName"/>: for each step (by its
/// <see cref="IUpgradeRescanStep.Id"/>) the datasets rescanned for it (by their internal reference, which a rename
/// does not change) and whether the job went through every dataset for it. A step registered later finds no progress
/// and runs for every dataset; a step that is done does not run again.
/// </summary>
public sealed class UpgradeRescanState
{
    /// <summary>The progress of each step, by its id.</summary>
    public Dictionary<string, UpgradeRescanStepState>? Steps { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The first shape of the file, before the progress was kept per step: it recorded the build-key rescan only.
    /// Read by <see cref="Normalize"/>, never written.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Completed { get; set; }

    /// <inheritdoc cref="Completed"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Datasets { get; set; }

    /// <summary>
    /// Moves the progress of a file of the first shape (no step ids) to the step it recorded,
    /// <see cref="BuildKeyRescanStep"/>.
    /// </summary>
    public UpgradeRescanState Normalize()
    {
        Steps ??= new Dictionary<string, UpgradeRescanStepState>(StringComparer.Ordinal);
        if ((Completed || Datasets is { Count: > 0 }) && !Steps.ContainsKey(BuildKeyRescanStep.StepId))
            Steps[BuildKeyRescanStep.StepId] = new UpgradeRescanStepState { Completed = Completed, Datasets = Datasets ?? [] };

        Completed = false;
        Datasets = null;
        return this;
    }

    /// <summary>The progress of the step <paramref name="id"/>; none yet when it has never run.</summary>
    public UpgradeRescanStepState Step(string id)
    {
        Steps ??= new Dictionary<string, UpgradeRescanStepState>(StringComparer.Ordinal);
        if (!Steps.TryGetValue(id, out var step) || step == null)
            Steps[id] = step = new UpgradeRescanStepState();
        step.Datasets ??= [];
        return step;
    }
}

/// <summary>What one upgrade rescan step has done.</summary>
public sealed class UpgradeRescanStepState
{
    /// <summary>Every dataset was rescanned for the step: a later start does not run it (a dataset created after it needs none).</summary>
    public bool Completed { get; set; }

    /// <summary>The internal references of the datasets already rescanned for the step.</summary>
    public List<string> Datasets { get; set; } = [];
}
