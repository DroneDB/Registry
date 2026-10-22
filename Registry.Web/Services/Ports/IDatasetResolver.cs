using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Data.Models;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Organizations and datasets by slug (RQ-12, from <c>IUtils</c>). No access check: see
/// <see cref="IDatasetAccessResolver"/>, which resolves through this.
/// </summary>
public interface IDatasetResolver
{
    /// <summary>
    /// The organization <paramref name="orgSlug"/>. Throws <c>BadRequestException</c> for a missing or
    /// invalid slug and <c>NotFoundException</c> when there is no such organization, unless
    /// <paramref name="safe"/> (then null). Untracked unless <paramref name="withTracking"/>; its datasets
    /// are loaded only with <paramref name="withDatasets"/>.
    /// </summary>
    Task<Organization> GetOrganizationAsync(string orgSlug, bool safe = false, bool withTracking = false,
        bool withDatasets = false, CancellationToken ct = default);

    /// <summary>
    /// The dataset <paramref name="dsSlug"/> of <paramref name="orgSlug"/>, with its organization. Same
    /// errors as <see cref="GetOrganizationAsync"/>. A read-only lookup (the default) may come from the
    /// short-lived lookup cache, with the organization's members; a tracked one always queries.
    /// </summary>
    Task<Dataset> GetDatasetAsync(string orgSlug, string dsSlug, bool safe = false, bool withTracking = false,
        CancellationToken ct = default);

    /// <summary>The slug of <paramref name="orgName"/>, with a numeric suffix when an organization has it.</summary>
    string GetFreeOrganizationSlug(string orgName);
}
