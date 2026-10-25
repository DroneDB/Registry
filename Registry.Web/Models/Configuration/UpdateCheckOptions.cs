#nullable enable
using System.Text.Json.Serialization;

namespace Registry.Web.Models.Configuration;

/// <summary>What the update check e-mails the administrators about (<see cref="UpdateCheckOptions.UpdateCheckEmail"/>).</summary>
public enum UpdateCheckEmailMode
{
    /// <summary>Only the updates that carry security fixes.</summary>
    Security,

    /// <summary>Every new version.</summary>
    All,

    /// <summary>Never.</summary>
    Off
}

/// <summary>
/// The daily update check (keys of the <c>AppSettings</c> section): when it runs, where it asks, and what it e-mails
/// the administrators about.
/// </summary>
public sealed class UpdateCheckOptions
{
    /// <summary>The address of the update check service.</summary>
    public const string DefaultEndpoint = "https://dronedb.app/wp-json/dronedb/v1/update-check";

    /// <summary>
    /// Cron expression of the check. Empty: once a day, at a time that depends on the instance (so the deployments do
    /// not all ask in the same minute). "disabled", "off" or "none" switches the check off: nothing is sent.
    /// </summary>
    public string? UpdateCheckCron { get; set; }

    /// <summary>Address of the update check service: an absolute HTTPS URL. Override it for a test or staging service.</summary>
    public string UpdateCheckEndpoint { get; set; } = DefaultEndpoint;

    /// <summary>
    /// Which updates are e-mailed to the administrators: <c>security</c> (only updates with security fixes), <c>all</c> or
    /// <c>off</c>. Needs the e-mail server (<c>AppSettings:Smtp</c>).
    /// </summary>
    public string UpdateCheckEmail { get; set; } = "security";

    /// <summary>Whether the check runs.</summary>
    [JsonIgnore]
    public bool Enabled => !CronSetting.IsDisabled(UpdateCheckCron);

    /// <summary><see cref="UpdateCheckEmail"/> as a mode; null when it is not one of the three values.</summary>
    [JsonIgnore]
    public UpdateCheckEmailMode? EmailMode => ParseEmailMode(UpdateCheckEmail);

    /// <summary>The mode named by <paramref name="value"/> (any case); null when it is none of them.</summary>
    public static UpdateCheckEmailMode? ParseEmailMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "security" => UpdateCheckEmailMode.Security,
            "all" => UpdateCheckEmailMode.All,
            "off" => UpdateCheckEmailMode.Off,
            _ => null
        };
}
