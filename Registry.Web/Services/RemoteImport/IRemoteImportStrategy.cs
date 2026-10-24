#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.RemoteImport;

/// <summary>One dataset to import: where it is on the source Registry, the token to read it, where it goes.</summary>
public sealed record RemoteDataset(
    string RegistryUrl,
    string? AuthToken,
    string SourceOrganization,
    string SourceDataset,
    string DestinationOrganization,
    string DestinationDataset);

/// <summary>
/// A way to transfer one remote dataset (one per <see cref="ImportMode"/>): it creates the destination, writes the
/// files, indexes them, queues the dataset build and reports the outcome in the <see cref="ImportReport"/>. An
/// expected failure (a refused download, a file that fails) is reported; an unexpected one is thrown and reported
/// by <see cref="RemoteRegistryImporter"/>. Cancellation throws.
/// </summary>
public interface IRemoteImportStrategy
{
    ImportMode Mode { get; }

    Task ImportAsync(RemoteDataset dataset, ImportReport report, CancellationToken ct);
}
