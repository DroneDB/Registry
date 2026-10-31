namespace Registry.Web.Models.Configuration;

/// <summary>
/// How a cron setting of the <c>AppSettings</c> section is read: left empty it takes the job's default,
/// "disabled", "off" or "none" (any case) removes the job.
/// </summary>
public static class CronSetting
{
    /// <summary>Whether <paramref name="raw"/> switches the job off.</summary>
    public static bool IsDisabled(string? raw) => raw?.Trim().ToLowerInvariant() is "disabled" or "off" or "none";

    /// <summary>
    /// The cron expression to schedule: <paramref name="defaultCron"/> for a null or blank setting, null when the job
    /// is disabled (it must be removed), otherwise the setting as written.
    /// </summary>
    public static string? Resolve(string? raw, string defaultCron)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return defaultCron;

        return IsDisabled(raw) ? null : raw.Trim();
    }
}
