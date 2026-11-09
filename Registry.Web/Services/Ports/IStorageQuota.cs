using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Identity.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// The storage use and limit of the owner of an organization: the one storage quota of every path that adds files
/// (FR-XC-26, D-039). The quota that applies to a write is that of the owner of the target organization, whoever
/// writes (the owner or a member): the limiter setting and the administrator exemption are those of the owner, and the
/// role of the user who writes does not matter, so a job needs no current user.
/// </summary>
public interface IStorageQuota
{
    /// <summary>The storage <paramref name="user"/> uses (the datasets of the organizations it owns) and its limit.</summary>
    UserStorageInfo GetUserStorage(User user);

    /// <summary>The id of the owner of the organization <paramref name="orgSlug"/>, or null (no such organization, or none owned).</summary>
    Task<string?> OwnerIdOfAsync(string orgSlug, CancellationToken ct = default);

    /// <summary>
    /// The storage use and limit of the owner <paramref name="ownerId"/>, or null when no limit applies: the storage
    /// limiter is off, the owner is an administrator, the owner has no limit, or there is no owner (null or empty).
    /// </summary>
    Task<UserStorageInfo?> GetOwnerStorageLimitAsync(string? ownerId, CancellationToken ct = default);

    /// <summary>
    /// The bytes the owner <paramref name="ownerId"/> may still store, never negative, or null when no limit applies
    /// (<see cref="GetOwnerStorageLimitAsync"/>). The one place that applies the limiter and the administrator exemption.
    /// </summary>
    Task<long?> RemainingBytesAsync(string? ownerId, CancellationToken ct = default);

    /// <summary>
    /// Throws <c>QuotaExceededException</c> when <paramref name="size"/> more bytes would exceed the limit of the owner
    /// <paramref name="ownerId"/>.
    /// </summary>
    Task CheckOwnerStorageAsync(string? ownerId, long size = 0, CancellationToken ct = default);
}

/// <summary>The lookups by organization of <see cref="IStorageQuota"/>.</summary>
public static class StorageQuotaExtensions
{
    /// <summary>The bytes the owner of <paramref name="orgSlug"/> may still store, or null when no limit applies.</summary>
    public static async Task<long?> RemainingBytesOfOrganizationAsync(this IStorageQuota quota, string orgSlug,
        CancellationToken ct = default) =>
        await quota.RemainingBytesAsync(await quota.OwnerIdOfAsync(orgSlug, ct), ct);

    /// <summary>
    /// Throws <c>QuotaExceededException</c> when <paramref name="size"/> more bytes would exceed the limit of the owner of
    /// <paramref name="orgSlug"/>.
    /// </summary>
    public static async Task CheckOrganizationStorageAsync(this IStorageQuota quota, string orgSlug, long size = 0,
        CancellationToken ct = default) =>
        await quota.CheckOwnerStorageAsync(await quota.OwnerIdOfAsync(orgSlug, ct), size, ct);
}

/// <summary>An <see cref="IStorageQuota"/> without limits: for a context that has no storage service.</summary>
public sealed class UnlimitedStorageQuota : IStorageQuota
{
    public static readonly UnlimitedStorageQuota Instance = new();

    private UnlimitedStorageQuota()
    {
    }

    public UserStorageInfo GetUserStorage(User user) => new() { Used = 0 };

    public Task<string?> OwnerIdOfAsync(string orgSlug, CancellationToken ct = default) => Task.FromResult<string?>(null);

    public Task<UserStorageInfo?> GetOwnerStorageLimitAsync(string? ownerId, CancellationToken ct = default) =>
        Task.FromResult<UserStorageInfo?>(null);

    public Task<long?> RemainingBytesAsync(string? ownerId, CancellationToken ct = default) => Task.FromResult<long?>(null);

    public Task CheckOwnerStorageAsync(string? ownerId, long size = 0, CancellationToken ct = default) => Task.CompletedTask;
}
