#nullable enable
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Services.Import;

/// <summary>
/// Optional extension for import sources that support browsing the remote structure (listing
/// organizations or datasets) before an import is initiated. Only the <c>registry</c> source
/// implements this interface.
/// </summary>
public interface IBrowsableImportSource
{
    /// <summary>
    /// Lists the organizations available at the remote source endpoint.
    /// </summary>
    /// <param name="parameters">Source-specific connection parameters (url, username, password).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An array of browse items representing remote organizations.</returns>
    Task<RemoteBrowseItem[]> BrowseOrganizationsAsync(JsonElement parameters, CancellationToken ct);

    /// <summary>
    /// Lists the datasets available in the remote organization specified in the parameters.
    /// </summary>
    /// <param name="parameters">Source-specific parameters including the organization slug.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An array of browse items representing remote datasets.</returns>
    Task<RemoteBrowseItem[]> BrowseDatasetsAsync(JsonElement parameters, CancellationToken ct);
}

/// <summary>A slug/name pair returned by remote browse operations.</summary>
/// <param name="Slug">The item slug.</param>
/// <param name="Name">The human-readable name.</param>
public record RemoteBrowseItem(string Slug, string Name);
