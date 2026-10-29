using Registry.Web.Models.DTO;

namespace Registry.Web.Services.RemoteImport;

/// <summary>
/// The argument of <see cref="RemoteImportJob"/>: the source Registry, what to import from it and where to. Hangfire
/// stores job arguments as plain JSON, so the source password travels encrypted
/// (<see cref="Registry.Ports.Import.IImportCredentialProtector"/>).
/// </summary>
/// <param name="RegistryUrl">The source Registry (scheme and host only, validated before the job is queued).</param>
/// <param name="Username">The source user; with the password, the import authenticates, else it is anonymous.</param>
/// <param name="ProtectedPassword">The source password, encrypted; null for an anonymous import.</param>
/// <param name="SourceOrganization">The source organization.</param>
/// <param name="SourceDataset">The source dataset; null imports every dataset of the organization.</param>
/// <param name="DestinationOrganization">The local organization (created when missing).</param>
/// <param name="DestinationDataset">The local dataset of a one-dataset import; null keeps the source slug.</param>
/// <param name="Mode">How the files are transferred (<see cref="IRemoteImportStrategy"/>).</param>
public sealed record RemoteImportRequest(
    string RegistryUrl,
    string? Username,
    string? ProtectedPassword,
    string SourceOrganization,
    string? SourceDataset,
    string DestinationOrganization,
    string? DestinationDataset,
    ImportMode Mode);
