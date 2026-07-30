#nullable enable
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
    /// <see cref="EntryDto.BuildMissingDependencies"/>) on every buildable
    /// entry in <paramref name="entries"/>. Entries that are not buildable, or
    /// whose build is already complete, are left with a null status.
    /// </summary>
    /// <param name="orgSlug">Organization slug.</param>
    /// <param name="dsSlug">Dataset slug.</param>
    /// <param name="ddb">Opened DDB instance for the dataset.</param>
    /// <param name="entries">Entries to annotate in place.</param>
    Task AnnotateAsync(string orgSlug, string dsSlug, IDDB ddb, IReadOnlyList<EntryDto> entries);
}
