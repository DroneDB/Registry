#nullable enable
using System.Threading.Tasks;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Import;

/// <summary>The storage limit of the current user as the dataset import sees it (RQ-12, out of the import manager).</summary>
public interface IImportQuotaGate
{
    /// <summary>Throws <c>QuotaExceededException</c> when <paramref name="estimatedBytes"/> more bytes would exceed the user's limit.</summary>
    Task CheckAsync(long estimatedBytes);

    /// <summary>The bytes the current user may still store, which the worker enforces incrementally; null without a user or a limit.</summary>
    Task<long?> RemainingBytesAsync();
}

/// <summary>Default <see cref="IImportQuotaGate"/>.</summary>
public sealed class ImportQuotaGate : IImportQuotaGate
{
    private readonly IAuthManager _authManager;
    private readonly IStorageQuota _quota;

    public ImportQuotaGate(IAuthManager authManager, IStorageQuota quota)
    {
        _authManager = authManager;
        _quota = quota;
    }

    public Task CheckAsync(long estimatedBytes) => _quota.CheckCurrentUserStorage(estimatedBytes);

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
