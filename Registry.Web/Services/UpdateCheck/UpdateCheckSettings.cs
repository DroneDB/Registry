using System;
using System.Threading;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Storage;

namespace Registry.Web.Services.UpdateCheck;

/// <summary>
/// What an administrator told the update check (<c>.update-check-settings.json</c>, set in the Hub's "Usage and contact"
/// card).
/// </summary>
/// <param name="UseDeclaration">How the deployment is used, one of <see cref="UseDeclarations"/>; null when not stated.</param>
/// <param name="ContactEmail">The address to send security and release news to; null when the administrator did not agree to be contacted.</param>
/// <param name="ContactConsentAt">When the administrator agreed to be contacted on <see cref="ContactEmail"/> (UTC).</param>
/// <param name="LastPingEnqueuedAt">When a change of these settings last queued an update check (UTC): the 10 minute limit on those is shared by the nodes through this file.</param>
public sealed record UpdateCheckSettings(
    string? UseDeclaration,
    string? ContactEmail,
    DateTime? ContactConsentAt,
    DateTime? LastPingEnqueuedAt)
{
    /// <summary>The values of <see cref="UseDeclaration"/>: how the deployment is used, not which licence applies.</summary>
    public static readonly IReadOnlyList<string> UseDeclarations =
        ["personal", "nonprofit", "evaluation", "commercial_licensed", "commercial_needs_license"];

    /// <summary>Nothing stated.</summary>
    public static UpdateCheckSettings Empty { get; } = new(null, null, null, null);

    /// <summary>The address that may be sent: the one the administrator agreed to be contacted on.</summary>
    public string? ConsentedEmail => ContactConsentAt is null ? null : ContactEmail;
}

/// <summary>The settings file of the update check, in the storage folder every node shares.</summary>
public sealed class UpdateCheckSettingsStore
{
    private readonly StorageDocument<UpdateCheckSettings> _file;
    private readonly Lock _gate = new();

    public UpdateCheckSettingsStore(IOptions<StorageOptions> storage, IFileSystem fs, ILogger<UpdateCheckSettingsStore> logger) =>
        _file = new StorageDocument<UpdateCheckSettings>(storage, fs, MagicStrings.UpdateCheckSettingsFileName, logger);

    /// <summary>The settings; <see cref="UpdateCheckSettings.Empty"/> when none were saved.</summary>
    public UpdateCheckSettings Read() => _file.Read() ?? UpdateCheckSettings.Empty;

    /// <summary>Saves the result of <paramref name="change"/> (applied to the current settings, under a lock of this process).</summary>
    public void Update(Func<UpdateCheckSettings, UpdateCheckSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (_gate)
            _file.Write(change(Read()));
    }
}
