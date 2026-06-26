#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Orchestrates importing a remote dataset (another Registry instance or a downloadable archive) into a
/// newly created dataset. Verification is mandatory before an import can be created.
/// </summary>
public interface IImportManager
{
    /// <summary>
    /// Returns the import source types enabled on this deployment (registered AND allow-listed).
    /// </summary>
    /// <returns>The available source type identifiers.</returns>
    IReadOnlyList<string> GetAvailableSourceTypes();

    /// <summary>
    /// Verifies (probes) an import source: checks reachability/credentials and returns cheap metadata
    /// (estimated size, file count, a suggested name/slug) without creating anything.
    /// </summary>
    /// <param name="orgSlug">The destination organization slug.</param>
    /// <param name="request">The verify request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The probe result.</returns>
    Task<ImportVerifyResultDto> VerifyAsync(string orgSlug, VerifyImportRequestDto request, CancellationToken ct);

    /// <summary>
    /// Creates an empty dataset and submits a heavy task that imports the source into it.
    /// </summary>
    /// <param name="orgSlug">The destination organization slug.</param>
    /// <param name="request">The create request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created dataset and the tracking task id.</returns>
    Task<ImportCreateResultDto> CreateAsync(string orgSlug, CreateImportRequestDto request, CancellationToken ct);
}
