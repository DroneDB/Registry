using System.Collections.Generic;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Computes and annotates the build status (queued/building/pending/failed) of
/// buildable entries, surfacing why a build has not completed yet. Built
/// (ready) and non-buildable entries are left untouched (no status assigned):
/// "built" is the default state and is not transmitted to clients.
/// </summary>
public interface IBuildStatusService
{
    /// <summary>
    /// Annotates <see cref="EntryDto.BuildStatus"/> (and, for pending builds,
    /// <see cref="EntryDto.BuildMissingDependencies"/> and <see cref="EntryDto.BuildAwaitingFiles"/>) on every buildable
    /// entry in <paramref name="entries"/>, and <see cref="EntryDto.Artifacts"/> on every entry whose own
    /// build has artifacts. Entries that are not buildable, or whose build is already complete, are left with
    /// a null status.
    /// </summary>
    /// <param name="orgSlug">Organization slug.</param>
    /// <param name="dsSlug">Dataset slug.</param>
    /// <param name="ddb">Opened DDB instance for the dataset.</param>
    /// <param name="entries">Entries to annotate in place.</param>
    Task AnnotateAsync(string orgSlug, string dsSlug, IDDB ddb, IReadOnlyList<EntryDto> entries);

    /// <summary>
    /// The build status <see cref="AnnotateAsync"/> gives the entry <paramref name="path"/> of type
    /// <paramref name="type"/> (one of <see cref="EntryBuildStatuses"/>), or null when it has none.
    /// </summary>
    Task<string?> GetStatusAsync(string orgSlug, string dsSlug, IDDB ddb, string path, EntryType type);
}

/// <summary>The values of <see cref="EntryDto.BuildStatus"/>.</summary>
public static class EntryBuildStatuses
{
    public const string Queued = "queued";
    public const string Building = "building";
    public const string Pending = "pending";
    public const string Failed = "failed";
}
