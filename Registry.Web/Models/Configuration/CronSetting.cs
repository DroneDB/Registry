using System.Collections.Generic;
using System.Linq;

namespace Registry.Web.Models.Configuration;

/// <summary>
/// How a cron setting of the <c>AppSettings</c> section is read: left empty it takes the job's default,
/// "disabled", "off" or "none" (any case) removes the job; any other value is a cron expression of
/// <see cref="MinFields"/> or <see cref="MaxFields"/> fields, or one of the <see cref="Macros"/>. A setting is scheduled by
/// Hangfire.Core (its Cronos parser), so these are the forms that parser reads; the tests run them through it.
/// </summary>
public static class CronSetting
{
    /// <summary>The words that switch a job off, in lowercase (a setting matches them in any case).</summary>
    public static IReadOnlyList<string> DisableWords { get; } = ["disabled", "off", "none"];

    /// <summary>The fewest fields of a cron expression (minute, hour, day of month, month, day of week).</summary>
    public const int MinFields = 5;

    /// <summary>The most fields of a cron expression (a leading seconds field is accepted).</summary>
    public const int MaxFields = 6;

    /// <summary>
    /// The macros that stand for a whole expression (a macro is the whole value, in any letter case): the ones Cronos reads
    /// and Hangfire accepts, in lowercase.
    /// </summary>
    public static IReadOnlyList<string> Macros { get; } =
        ["@yearly", "@annually", "@monthly", "@weekly", "@daily", "@midnight", "@hourly", "@every_minute", "@every_second"];

    /// <summary>Whether <paramref name="raw"/> switches the job off.</summary>
    public static bool IsDisabled(string? raw) =>
        raw != null && DisableWords.Contains(raw.Trim().ToLowerInvariant());

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
