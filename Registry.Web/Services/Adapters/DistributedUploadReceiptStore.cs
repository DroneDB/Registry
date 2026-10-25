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
/// Keeps the outcome of uploads by client-generated id: when a proxy times out after
/// the last byte (504) while the Registry is still adding the file, the client asks here instead
/// of uploading the whole file again.
/// </summary>
public sealed class DistributedUploadReceiptStore(IDistributedCache cache, IOptions<UploadSettings> settings)
{
    private readonly TimeSpan _ttl = TimeSpan.FromHours(Math.Max(1, settings.Value.ReceiptHours));

    public Task<UploadReceipt?> GetAsync(string orgSlug, string dsSlug, string uploadId) =>
        cache.GetRecordAsync<UploadReceipt?>(Key(orgSlug, dsSlug, uploadId));

    public Task SetAsync(string orgSlug, string dsSlug, string uploadId, UploadReceipt receipt) =>
        cache.SetRecordAsync(Key(orgSlug, dsSlug, uploadId), receipt, _ttl);

    public Task RemoveAsync(string orgSlug, string dsSlug, string uploadId) =>
        cache.RemoveAsync(Key(orgSlug, dsSlug, uploadId));

    private static string Key(string orgSlug, string dsSlug, string uploadId) => CacheCategories.UploadReceipt(orgSlug, dsSlug, uploadId);
}
