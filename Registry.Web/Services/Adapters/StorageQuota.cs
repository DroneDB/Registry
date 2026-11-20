using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Default <see cref="IStorageQuota"/>: the sizes of the datasets the owner of an organization owns against the limit of
/// that owner (FR-XC-26). Reads the identity and registry databases only (no request, no current user), so it is the
/// same on the web host and on a job host.
/// </summary>
public class StorageQuota : IStorageQuota
{
    private readonly RegistryContext _context;
    private readonly ApplicationDbContext _identity;
    private readonly AccountOptions _accounts;
    private readonly IDatasetSummaryProvider _summaries;

    public StorageQuota(RegistryContext context, ApplicationDbContext identity, IOptions<AccountOptions> accounts,
        IDatasetSummaryProvider summaries)
    {
        _context = context;
        _identity = identity;
        _accounts = accounts.Value;
        _summaries = summaries;
    }

    public UserStorageInfo GetUserStorage(User user)
    {
        if (user == null)
            throw new ArgumentException("User is null", nameof(user));

        // The limit shown is the one that is enforced: with the limiter off, or for an administrator, there is none (the
        // stored value stays in the metadata and in the user list, where it is edited)
        var enforced = _accounts.EnableStorageLimiter
                       && !IdentityRoleQuery.UserIdsIn(_identity, ApplicationDbContext.AdminRoleName).Any(id => id == user.Id);

        // Get all the datasets that belong to the user - using projection for efficiency
        var datasets = _context.Organizations
            .AsNoTracking()
            .Where(org => org.OwnerId == user.Id)
            .SelectMany(org => org.Datasets)
            .Select(dataset => new { OrgSlug = dataset.Organization.Slug, dataset.InternalRef })
            .ToArray();

        // Sizes are cached per dataset until its index changes: no native call per upload chunk
        var size = datasets.Sum(ds => _summaries.Get(ds.OrgSlug, ds.InternalRef).Size);

        return new UserStorageInfo
        {
            Total = enforced ? user.StoredQuotaBytes() : null,
            Used = size
        };
    }

    public async Task<string?> OwnerIdOfAsync(string orgSlug, CancellationToken ct = default) =>
        await _context.Organizations.AsNoTracking()
            .Where(org => org.Slug == orgSlug)
            .Select(org => org.OwnerId)
            .FirstOrDefaultAsync(ct);

    public async Task<UserStorageInfo?> GetOwnerStorageLimitAsync(string? ownerId, CancellationToken ct = default)
    {
        if (!_accounts.EnableStorageLimiter || string.IsNullOrEmpty(ownerId))
            return null;

        // An administrator owner has no limit, whoever writes
        var admins = IdentityRoleQuery.UserIdsIn(_identity, ApplicationDbContext.AdminRoleName);
        if (await admins.AnyAsync(id => id == ownerId, ct))
            return null;

        var owner = await _identity.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == ownerId, ct);
        if (owner == null)
            return null;

        var storageInfo = GetUserStorage(owner);
        return storageInfo.Total == null ? null : storageInfo;
    }

    public async Task<long?> RemainingBytesAsync(string? ownerId, CancellationToken ct = default)
    {
        var info = await GetOwnerStorageLimitAsync(ownerId, ct);
        return info == null ? null : Math.Max(0, info.Total!.Value - info.Used);
    }

    public async Task CheckOwnerStorageAsync(string? ownerId, long size = 0, CancellationToken ct = default)
    {
        var info = await GetOwnerStorageLimitAsync(ownerId, ct);
        if (info == null) return;

        var remaining = info.Total!.Value - info.Used;
        if (size > remaining)
            throw QuotaExceededException.OwnerOverQuota(remaining);
    }
}
