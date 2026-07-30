#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Web-layer orchestration for importing a single file from a URL into an existing dataset. Verifies
/// the URL cheaply (probe) and, on import, encrypts the optional basic-auth credential and submits the
/// <c>import-file</c> heavy task.
/// </summary>
public interface IFileUrlImportManager
{
    /// <summary>
    /// Probes the URL without transferring the body and reports reachability, size, the derived file
    /// name and the outcome of the deny-list / size checks.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="request">The verify request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The verification result.</returns>
    Task<UrlImportVerifyResultDto> VerifyAsync(string orgSlug, string dsSlug,
        UrlImportVerifyRequestDto request, CancellationToken ct);

    /// <summary>
    /// Submits the <c>import-file</c> heavy task after re-validating the request and encrypting the
    /// optional basic-auth password.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="request">The import request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The submitted task id.</returns>
    Task<UrlImportResultDto> ImportAsync(string orgSlug, string dsSlug,
        UrlImportRequestDto request, CancellationToken ct);
}
