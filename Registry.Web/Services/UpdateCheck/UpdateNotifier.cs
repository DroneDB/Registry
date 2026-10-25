#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Email.Templates;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.UpdateCheck;

/// <summary>
/// <see cref="IUpdateNotifier"/> that e-mails the administrators about a new version, once per version. What it sends
/// is decided by <see cref="UpdateCheckOptions.UpdateCheckEmail"/>; the send goes through <see cref="IEmailService"/>
/// (no data leaves for the update check service) and is immediate, because the version is recorded as notified only when
/// the e-mail was handed to the mail server. Anything that stops the e-mail leaves nothing recorded, so the next check
/// tries again.
/// </summary>
public sealed class UpdateNotifier : IUpdateNotifier
{
    private readonly UpdateCheckOptions _options;
    private readonly IEmailService _email;
    private readonly IAdminRecipients _admins;
    private readonly IUpdateCheckCache _cache;
    private readonly ILogger<UpdateNotifier> _logger;

    public UpdateNotifier(IOptions<UpdateCheckOptions> options, IEmailService email, IAdminRecipients admins,
        IUpdateCheckCache cache, ILogger<UpdateNotifier> logger)
    {
        _options = options.Value;
        _email = email;
        _admins = admins;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task NotifyAsync(UpdateInfoDto update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (!IsWorthAnEmail(update) || update.LatestVersion is not { } latest)
            return;

        // Before the recipients are read and the e-mail is rendered: nothing can be sent
        if (!_email.IsEnabled)
        {
            _logger.LogDebug("E-mail is off: the administrators are not told about Registry {LatestVersion}", latest);
            return;
        }

        var recipients = await _admins.GetAsync(ct).ConfigureAwait(false);
        if (recipients.Count == 0)
        {
            _logger.LogDebug("No administrator can be e-mailed about Registry {LatestVersion}", latest);
            return;
        }

        var model = new UpdateAvailableEmailModel(update.CurrentVersion, latest, update.IsSecurityUpdate, update.ReleaseNotes,
            update.CompareUrl);

        bool sent;
        try
        {
            sent = await _email.SendNowAsync(UpdateAvailableEmailTemplate.Instance, model, recipients, ct).ConfigureAwait(false);
        }
        catch (EmailDeliveryException ex)
        {
            _logger.LogWarning(ex, "Cannot e-mail the administrators about Registry {LatestVersion}; the next check tries again", latest);
            return;
        }

        if (!sent)
        {
            // E-mail was switched off between the check above and the send
            _logger.LogDebug("E-mail is off: the administrators are not told about Registry {LatestVersion}", latest);
            return;
        }

        _cache.Update(cache => cache with { LastNotifiedVersion = latest });
        _logger.LogInformation("The administrators were e-mailed about Registry {LatestVersion}", latest);
    }

    // The mode, the update and what was told before; no I/O
    private bool IsWorthAnEmail(UpdateInfoDto update)
    {
        switch (_options.EmailMode)
        {
            case null or UpdateCheckEmailMode.Off:
                return false;
            case UpdateCheckEmailMode.Security when !update.IsSecurityUpdate:
                return false;
        }

        if (!update.IsUpdateAvailable || string.IsNullOrWhiteSpace(update.LatestVersion))
            return false;

        // Newer than the last version told about; a last version that is not a version (a damaged file) counts as none
        var lastNotified = _cache.Read().LastNotifiedVersion;
        return !Version.TryParse(lastNotified, out _) || UpdateInfoBuilder.IsNewer(update.LatestVersion, lastNotified);
    }
}
