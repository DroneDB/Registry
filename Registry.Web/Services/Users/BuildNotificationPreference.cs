using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Registry.Common;
using Registry.Web.Exceptions;
using Registry.Web.Identity.Models;
using Registry.Web.Services.BuildNotifications;

namespace Registry.Web.Services.Users;

/// <summary>Which build notification e-mails a user wants.</summary>
public enum BuildNotificationLevel
{
    /// <summary>None (the default: the e-mails are opt-in).</summary>
    None,

    /// <summary>Every completed and every failed build.</summary>
    All,

    /// <summary>
    /// Only the builds that failed, and the builds that wait for missing files; for the task notifications
    /// (<see cref="BuildNotificationPreference.TasksField"/>), only the tasks that failed.
    /// </summary>
    Failed
}

/// <summary>
/// The e-mail preferences the server reads: <c>notifications.builds</c> (which ended builds it e-mails) and
/// <c>notifications.tasks</c> (which ended tasks, FR-PRC-40) of <see cref="User.Preferences"/>, each one of <c>"none"</c>,
/// <c>"all"</c>, <c>"failed"</c>. A missing section, field or an unknown value is <see cref="BuildNotificationLevel.None"/>,
/// which is also what the Hub's reset (deleting the section) gives.
/// </summary>
public static class BuildNotificationPreference
{
    /// <summary>The preferences section.</summary>
    public const string Section = "notifications";

    /// <summary>The field of the section that chooses the build e-mails.</summary>
    public const string BuildsField = "builds";

    /// <summary>The field of the section that chooses the task e-mails (a long task that ended or failed).</summary>
    public const string TasksField = "tasks";

    private static readonly string[] Fields = [BuildsField, TasksField];

    private static readonly IReadOnlyDictionary<string, BuildNotificationLevel> Values =
        new Dictionary<string, BuildNotificationLevel>(StringComparer.Ordinal)
        {
            ["none"] = BuildNotificationLevel.None,
            ["all"] = BuildNotificationLevel.All,
            ["failed"] = BuildNotificationLevel.Failed
        };

    /// <summary>What <paramref name="user"/> chose for the build e-mails.</summary>
    public static BuildNotificationLevel Of(User user) => Read(user, BuildsField);

    /// <summary>What <paramref name="user"/> chose for the task e-mails.</summary>
    public static BuildNotificationLevel TasksOf(User user) => Read(user, TasksField);

    private static BuildNotificationLevel Read(User user, string field)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!user.Preferences.TryGetValue(Section, out var section))
            return BuildNotificationLevel.None;

        // Read back from the column it is a dictionary; a value set in this request may still be the JSON element
        var fields = section is JsonElement element ? PlainValue.From(element) : section;

        return fields is IDictionary<string, object?> map && map.TryGetValue(field, out var value) &&
               value is string text && Values.TryGetValue(text, out var level)
            ? level
            : BuildNotificationLevel.None;
    }

    /// <summary>
    /// Whether a user with <paramref name="level"/> is told about a build that ended with <paramref name="outcome"/>: a
    /// build that waits for missing files needs the user like a failure, so it goes with the failures.
    /// </summary>
    public static bool Wants(this BuildNotificationLevel level, BuildOutcome outcome) =>
        level == BuildNotificationLevel.All ||
        (level == BuildNotificationLevel.Failed && outcome is BuildOutcome.Failed or BuildOutcome.WaitingForFiles);

    /// <summary>
    /// Whether a user with <paramref name="level"/> (the task choice) is told about a task that <paramref name="succeeded"/>
    /// or failed: "all" gets both, "failed" only the failures.
    /// </summary>
    public static bool WantsTask(this BuildNotificationLevel level, bool succeeded) =>
        level == BuildNotificationLevel.All || (level == BuildNotificationLevel.Failed && !succeeded);

    /// <summary>
    /// The <see cref="Section"/> section after a patch: the stored fields, with each field <paramref name="patch"/> gives a
    /// value for replaced. A field the patch lacks or sends as null keeps the stored value, so a device that knows one field
    /// (or a Hub that sends a null it has no value for) cannot overwrite the other field another device set. Deleting the
    /// whole section (null) is how the user goes back to the defaults.
    /// </summary>
    /// <param name="stored">The stored section as the column gives it (a dictionary), or whatever it holds.</param>
    /// <param name="patch">The validated section of the patch (<see cref="Validate"/>).</param>
    public static Dictionary<string, object?> Merge(object? stored, JsonElement patch)
    {
        var current = stored is JsonElement element ? PlainValue.From(element) : stored;
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (current is IDictionary<string, object?> map)
            foreach (var field in Fields)
                if (map.TryGetValue(field, out var value) && value != null)
                    merged[field] = value;

        foreach (var field in patch.EnumerateObject())
            if (field.Value.ValueKind != JsonValueKind.Null)
                merged[field.Name] = PlainValue.From(field.Value);

        return merged;
    }

    /// <summary>
    /// Checks the <see cref="Section"/> section of a preferences patch (not null: null deletes it): an object whose fields
    /// are <see cref="BuildsField"/> and <see cref="TasksField"/>, each with one of the known values or null (no change:
    /// see <see cref="Merge"/>).
    /// </summary>
    /// <exception cref="BadRequestException">The section is not valid; the message says why.</exception>
    public static void Validate(JsonElement section)
    {
        if (section.ValueKind != JsonValueKind.Object)
            throw new BadRequestException($"Section '{Section}' is an object");

        foreach (var field in section.EnumerateObject())
        {
            if (!Fields.Contains(field.Name))
                throw new BadRequestException($"Section '{Section}' has no field '{field.Name}'; its fields are {string.Join(" and ", Fields.Select(f => $"'{f}'"))}");

            // An old Hub sends every field of the section, null for the one it has no value for: that is no change
            if (field.Value.ValueKind == JsonValueKind.Null)
                continue;

            if (field.Value.ValueKind != JsonValueKind.String || !Values.ContainsKey(field.Value.GetString()!))
                throw new BadRequestException(
                    $"'{Section}.{field.Name}' is one of {string.Join(", ", Values.Keys.Select(k => $"\"{k}\""))}, not {field.Value.GetRawText()}");
        }
    }
}
