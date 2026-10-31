using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Organizations and datasets by slug (split from the former utilities service). No access check:
/// see <see cref="IDatasetAccessResolver"/>, which resolves through this.
/// </summary>
public interface IDatasetResolver
{
    /// <summary>
    /// The organization <paramref name="orgSlug"/>. Throws <c>BadRequestException</c> for a missing or
    /// invalid slug and <c>NotFoundException</c> when there is no such organization, unless
    /// <paramref name="safe"/> (then null). Untracked unless <paramref name="withTracking"/>; its datasets
    /// are loaded only with <paramref name="withDatasets"/>.
    /// </summary>
    Task<Organization?> GetOrganizationAsync(string orgSlug, bool safe = false, bool withTracking = false,
        bool withDatasets = false, CancellationToken ct = default);

    /// <summary>
    /// The dataset <paramref name="dsSlug"/> of <paramref name="orgSlug"/>, with its organization. Same
    /// errors as <see cref="GetOrganizationAsync"/>. A read-only lookup (the default) may come from the
    /// short-lived lookup cache, with the organization's members; a tracked one always queries.
    /// </summary>
    Task<Dataset?> GetDatasetAsync(string orgSlug, string dsSlug, bool safe = false, bool withTracking = false,
        CancellationToken ct = default);

    /// <summary>The slug of <paramref name="orgName"/>, with a numeric suffix when an organization has it.</summary>
    Task<string> GetFreeOrganizationSlugAsync(string orgName, CancellationToken ct = default);
}

/// <summary>
/// The lookups of <see cref="IDatasetResolver"/> that throw when the organization or the dataset is missing, typed
/// as never null: the resolver returns null only when asked to be safe.
/// </summary>
public static class DatasetResolverExtensions
{
    /// <summary>The message of the <see cref="NotFoundException"/> for a missing organization.</summary>
    public const string OrganizationNotFoundMessage = "Organization not found";

    /// <summary>The message of the <see cref="NotFoundException"/> for a missing dataset.</summary>
    public static string DatasetNotFoundMessage(string orgSlug, string dsSlug) =>
        $"Cannot find dataset {dsSlug} in organization {orgSlug}";

    /// <summary><see cref="IDatasetResolver.GetOrganizationAsync"/> without safe: throws when the organization is missing.</summary>
    public static async Task<Organization> GetRequiredOrganizationAsync(this IDatasetResolver resolver, string orgSlug,
        bool withTracking = false, bool withDatasets = false, CancellationToken ct = default) =>
        await resolver.GetOrganizationAsync(orgSlug, safe: false, withTracking, withDatasets, ct)
        ?? throw new NotFoundException(OrganizationNotFoundMessage);

    /// <summary><see cref="IDatasetResolver.GetDatasetAsync"/> without safe: throws when the dataset is missing.</summary>
    public static async Task<Dataset> GetRequiredDatasetAsync(this IDatasetResolver resolver, string orgSlug,
        string dsSlug, bool withTracking = false, CancellationToken ct = default) =>
        await resolver.GetDatasetAsync(orgSlug, dsSlug, safe: false, withTracking, ct)
        ?? throw new NotFoundException(DatasetNotFoundMessage(orgSlug, dsSlug));
}
