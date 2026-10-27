#nullable enable
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.UpdateCheck;

/// <summary>
/// <see cref="IUpdateCheckService"/>: builds the payload, posts it, remembers a good answer and tells the notifier. Every
/// run asks again, whatever the last result was. A failure (error, timeout, redirect, 4xx, 5xx, an answer too large or not
/// usable) keeps the previous good answer, records when it happened and is logged at debug level: the deployment works the
/// same without the update check service.
/// </summary>
public sealed class UpdateCheckService : IUpdateCheckService
{
    private readonly UpdateCheckOptions _options;
    private readonly UpdateCheckExchange _exchange;
    private readonly IUpdateCheckCache _cache;
    private readonly UpdateInfoBuilder _info;
    private readonly IUpdateNotifier _notifier;
    private readonly TimeProvider _time;
    private readonly ILogger<UpdateCheckService> _logger;

    public UpdateCheckService(IOptions<UpdateCheckOptions> options, UpdateCheckExchange exchange, IUpdateCheckCache cache,
        UpdateInfoBuilder info, IUpdateNotifier notifier, TimeProvider time, ILogger<UpdateCheckService> logger)
    {
        _options = options.Value;
        _exchange = exchange;
        _cache = cache;
        _info = info;
        _notifier = notifier;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task CheckForUpdatesAsync(CancellationToken ct)
    {
        // A switched-off check sends nothing, whoever asks (the schedule, a changed setting, a stale queued job)
        if (!_options.Enabled)
        {
            _logger.LogDebug("The update check is disabled: nothing is sent");
            return;
        }

        UpdateCheckResult result;
        try
        {
            var response = await _exchange.RunAsync(ct).ConfigureAwait(false);
            result = new UpdateCheckResult(response, _time.GetUtcNow().UtcDateTime);
            _cache.Update(cache => cache with { LastResult = result, LastFailureAt = null });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (UpdateCheckException ex)
        {
            Failed(ex);
            return;
        }
        catch (StorageDocumentException ex)
        {
            Failed(ex);
            return;
        }

        _logger.LogDebug("Update check: latest version {LatestVersion}", result.Response.LatestVersion);
        await NotifyAsync(result, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public UpdateInfoDto GetUpdateInfo() => _info.Build(_cache.Read().LastResult);

    private void Failed(Exception ex)
    {
        _logger.LogDebug(ex, "Update check failed");

        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            _cache.Update(cache => cache with { LastFailureAt = now });
        }
        catch (StorageDocumentException cacheEx)
        {
            _logger.LogDebug(cacheEx, "Cannot record the failure of the update check");
        }
    }

    // The check itself succeeded; a notification that fails is retried by the next check
    private void NotificationFailed(Exception ex) =>
        _logger.LogWarning(ex, "Cannot notify about the result of the update check");

    private async Task NotifyAsync(UpdateCheckResult result, CancellationToken ct)
    {
        try
        {
            await _notifier.NotifyAsync(_info.Build(result), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (StorageDocumentException ex)
        {
            NotificationFailed(ex);
        }
        catch (DbException ex)
        {
            NotificationFailed(ex);
        }
    }
}
