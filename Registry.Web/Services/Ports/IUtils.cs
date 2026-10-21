using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Utility interface for organization/dataset lookup, URL generation, and storage checks.
/// </summary>
public interface IUtils
{
    /// <summary>
    /// The organization <paramref name="orgSlug"/>. Throws <c>BadRequestException</c> for a missing or
    /// invalid slug and <c>NotFoundException</c> when there is no such organization, unless
    /// <paramref name="safe"/> (then null). Untracked unless <paramref name="withTracking"/>; its datasets
    /// are loaded only with <paramref name="withDatasets"/>. No access check: see
    /// <see cref="IDatasetAccessResolver"/>.
    /// </summary>
    Task<Organization> GetOrganizationAsync(string orgSlug, bool safe = false, bool withTracking = false,
        bool withDatasets = false, CancellationToken ct = default);

    /// <summary>
    /// The dataset <paramref name="dsSlug"/> of <paramref name="orgSlug"/>, with its organization. Same
    /// errors as <see cref="GetOrganizationAsync"/>. A read-only lookup (the default) may come from the
    /// short-lived lookup cache, with the organization's members; a tracked one always queries. No access
    /// check: see <see cref="IDatasetAccessResolver"/>.
    /// </summary>
    Task<Dataset> GetDatasetAsync(string orgSlug, string dsSlug, bool safe = false, bool withTracking = false,
        CancellationToken ct = default);

    string GetFreeOrganizationSlug(string orgName);
    string GenerateDatasetUrl(Dataset dataset, bool useDdbScheme = false);

    UserStorageInfo GetUserStorage(User user);

    /// <summary>
    /// The storage use and limit of the current user, or null when no limit applies (storage
    /// limiter off, administrator, or no limit set).
    /// </summary>
    Task<UserStorageInfo> GetCurrentUserStorageLimit();

    /// <summary>
    /// Throws <c>QuotaExceededException</c> when <paramref name="size"/> more bytes would exceed
    /// the current user's limit (see <see cref="GetCurrentUserStorageLimit"/>).
    /// </summary>
    Task CheckCurrentUserStorage(long size = 0);

    string GenerateDatasetStacUrl(string orgSlug, string dsSlug);
    string GenerateStacUrl();
    string GetLocalHost(bool useDdbScheme = false);
}