#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>Whether an equivalent task is already in the history (RQ-12, out of <c>HeavyTaskRunner</c>).</summary>
public interface IHeavyTaskDedup
{
    /// <summary>
    /// The id of a task of the dataset with the same tool and request hash that was submitted within the lookback
    /// window and can be reused, or null (none, or deduplication is off).
    /// </summary>
    Task<string?> FindAsync(string orgSlug, string dsSlug, string toolId, string requestHash, CancellationToken ct);
}

/// <summary>Default <see cref="IHeavyTaskDedup"/>: the job index within <see cref="ProcessingPlatformSettings.DedupLookbackHours"/>.</summary>
public sealed class HeavyTaskDedup : IHeavyTaskDedup
{
    private readonly IJobIndexQuery _query;
    private readonly ProcessingPlatformSettings _settings;

    public HeavyTaskDedup(IJobIndexQuery query, IOptions<ProcessingPlatformSettings> settings)
    {
        _query = query;
        _settings = settings.Value;
    }

    public async Task<string?> FindAsync(string orgSlug, string dsSlug, string toolId, string requestHash,
        CancellationToken ct)
    {
        if (!_settings.DedupEnabled)
            return null;

        var candidate = await _query.FindDedupCandidateAsync(orgSlug, dsSlug, toolId, requestHash,
            _settings.DedupLookbackHours, ct);
        return candidate?.JobId;
    }
}
