using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>Default <see cref="IStorageQuota"/>: the sizes of the datasets a user owns against its limit.</summary>
public class StorageQuota : IStorageQuota
{
    private readonly IAuthManager _authManager;
    private readonly RegistryContext _context;
    private readonly AccountOptions _accounts;
    private readonly IDatasetSummaryProvider _summaries;

    public StorageQuota(IAuthManager authManager, RegistryContext context, IOptions<AccountOptions> accounts,
        IDatasetSummaryProvider summaries)
    {
        _authManager = authManager;
        _context = context;
        _accounts = accounts.Value;
        _summaries = summaries;
    }

    public UserStorageInfo GetUserStorage(User user)
    {
        if (user == null)
            throw new ArgumentException("User is null", nameof(user));

        var maxStorage = user.Metadata?.SafeGetValue(MagicStrings.MaxStorageKey) is long obj ? obj : (long?)null;

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
            // Max storage is in MB, we need bytes to stay consistent
            Total = maxStorage * 1024 * 1024,
            Used = size
        };
    }

    public async Task<UserStorageInfo?> GetCurrentUserStorageLimit()
    {
        if (!_accounts.EnableStorageLimiter) return null;

        // Admins do not have limits
        if (await _authManager.IsUserAdmin()) return null;

        var storageInfo = GetUserStorage(await _authManager.GetCurrentUser());
        return storageInfo.Total == null ? null : storageInfo;
    }

    public async Task CheckCurrentUserStorage(long size = 0)
    {
        var storageInfo = await GetCurrentUserStorageLimit();
        if (storageInfo == null) return;

        var currentUsage = storageInfo.Used + size;

        if (currentUsage > storageInfo.Total)
            throw new QuotaExceededException(currentUsage, storageInfo.Total);
    }
}
