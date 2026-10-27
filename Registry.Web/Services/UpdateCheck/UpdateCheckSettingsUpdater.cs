#nullable enable
using System;
using System.Linq;
using System.Threading;
using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Models.Email;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.UpdateCheck;

/// <summary>
/// Saves what an administrator tells the update check (how the deployment is used, who to contact), and lets the
/// next check carry it: a change queues one check, at most one every <see cref="PingWindow"/> however often the
/// administrator saves, and none while the update check is off.
/// </summary>
public sealed class UpdateCheckSettingsUpdater
{
    /// <summary>The longest address that fits the contract.</summary>
    public const int MaxEmailLength = 254;

    /// <summary>The shortest time between two checks queued by changes of the settings.</summary>
    public static readonly TimeSpan PingWindow = TimeSpan.FromMinutes(10);

    private readonly UpdateCheckSettingsStore _settings;
    private readonly IBackgroundJobClient _jobs;
    private readonly UpdateCheckOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<UpdateCheckSettingsUpdater> _logger;

    public UpdateCheckSettingsUpdater(UpdateCheckSettingsStore settings, IBackgroundJobClient jobs,
        IOptions<UpdateCheckOptions> options, TimeProvider time, ILogger<UpdateCheckSettingsUpdater> logger)
    {
        _settings = settings;
        _jobs = jobs;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Validates <paramref name="request"/> and saves it. An address needs the administrator's consent; an empty one removes
    /// the address and the consent. The consent keeps its time unless the address changes.
    /// </summary>
    /// <exception cref="BadRequestException">The declaration is not one of the known values, the address is not valid, or an address came without consent.</exception>
    public void Apply(UpdateCheckSettingsDto request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var declaration = ValidDeclaration(request.UseDeclaration);
        var email = ValidEmail(request.ContactEmail, request.ContactConsent);
        var now = _time.GetUtcNow().UtcDateTime;

        var queue = false;
        DateTime? previousPing = null;

        _settings.Update(current =>
        {
            DateTime? consentAt = email is null ? null
                : string.Equals(current.ContactEmail, email, StringComparison.Ordinal) ? current.ContactConsentAt ?? now
                : now;
            var next = current with { UseDeclaration = declaration, ContactEmail = email, ContactConsentAt = consentAt };

            var changed = next.UseDeclaration != current.UseDeclaration || next.ContactEmail != current.ContactEmail;
            queue = changed && MayQueueCheck(current, now);
            previousPing = current.LastPingEnqueuedAt;
            return queue ? next with { LastPingEnqueuedAt = now } : next;
        });

        // Queued only once the file holds the new settings: a worker that picks the check up at once reads them
        if (queue)
            QueueCheck(now, previousPing);
    }

    private bool MayQueueCheck(UpdateCheckSettings current, DateTime now)
    {
        if (!_options.Enabled)
            return false;

        // The time of the last queued check is in the shared file, so the limit holds across the nodes
        return current.LastPingEnqueuedAt is not { } last || now - last >= PingWindow;
    }

    private void QueueCheck(DateTime now, DateTime? previousPing)
    {
        try
        {
            _jobs.Enqueue<IUpdateCheckService>(service => service.CheckForUpdatesAsync(CancellationToken.None));
        }
        catch (BackgroundJobClientException ex)
        {
            // The settings are saved and the daily check carries them; the window was not used, so give it back
            // unless another save has taken it since
            _logger.LogWarning(ex, "Cannot queue an update check after the settings changed");
            _settings.Update(current => current.LastPingEnqueuedAt == now
                ? current with { LastPingEnqueuedAt = previousPing }
                : current);
        }
    }

    private static string? ValidDeclaration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var declaration = value.Trim();
        return UpdateCheckSettings.UseDeclarations.Contains(declaration)
            ? declaration
            : throw new BadRequestException(
                $"useDeclaration must be one of {string.Join(", ", UpdateCheckSettings.UseDeclarations)}, or empty", noRetry: true);
    }

    private static string? ValidEmail(string? value, bool consent)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var email = value.Trim();
        if (!consent)
            throw new BadRequestException("contactEmail needs contactConsent: the address is used only if you agree to be contacted", noRetry: true);
        if (email.Length > MaxEmailLength || !EmailAddress.IsValid(email))
            throw new BadRequestException("contactEmail is not a valid e-mail address", noRetry: true);

        return email;
    }
}
