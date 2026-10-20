#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// <see cref="IUploadReceiptStore"/> on the distributed cache (shared by every instance), kept
/// for <see cref="UploadSettings.ReceiptHours"/>.
/// </summary>
public sealed class DistributedUploadReceiptStore(IDistributedCache cache, IOptions<AppSettings> settings)
    : IUploadReceiptStore
{
    private readonly TimeSpan _ttl = TimeSpan.FromHours(Math.Max(1, (settings.Value.Uploads ?? new UploadSettings()).ReceiptHours));

    public Task<UploadReceipt?> GetAsync(string orgSlug, string dsSlug, string uploadId) =>
        cache.GetRecordAsync<UploadReceipt?>(Key(orgSlug, dsSlug, uploadId));

    public Task SetAsync(string orgSlug, string dsSlug, string uploadId, UploadReceipt receipt) =>
        cache.SetRecordAsync(Key(orgSlug, dsSlug, uploadId), receipt, _ttl);

    public Task RemoveAsync(string orgSlug, string dsSlug, string uploadId) =>
        cache.RemoveAsync(Key(orgSlug, dsSlug, uploadId));

    private static string Key(string orgSlug, string dsSlug, string uploadId) => $"upload-receipt-{orgSlug}-{dsSlug}-{uploadId}";
}
