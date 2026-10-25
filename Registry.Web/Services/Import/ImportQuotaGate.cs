#nullable enable
using System.Threading.Tasks;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Import;

/// <summary>The storage limit of the current user as the dataset import sees it (out of the import manager).</summary>
public sealed class ImportQuotaGate
{
    private readonly IAuthManager _authManager;
    private readonly IStorageQuota _quota;

    public ImportQuotaGate(IAuthManager authManager, IStorageQuota quota)
    {
        _authManager = authManager;
        _quota = quota;
    }

    /// <summary>Throws <c>QuotaExceededException</c> when <paramref name="estimatedBytes"/> more bytes would exceed the user's limit.</summary>
    public Task CheckAsync(long estimatedBytes) => _quota.CheckCurrentUserStorage(estimatedBytes);

    /// <summary>The bytes the current user may still store, which the worker enforces incrementally; null without a user or a limit.</summary>
    public async Task<long?> RemainingBytesAsync()
    {
        var user = await _authManager.GetCurrentUser();
        if (user is null) return null;

        var info = _quota.GetUserStorage(user);
        if (info.Total is null) return null; // unlimited

        var remaining = info.Total.Value - info.Used;
        return remaining < 0 ? 0 : remaining;
    }
}
