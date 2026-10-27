#nullable enable
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

    /// <summary>Only the builds that failed.</summary>
    Failed
}

/// <summary>
/// The one preference the server reads: <c>notifications.builds</c> of <see cref="User.Preferences"/>, one of
/// <c>"none"</c>, <c>"all"</c>, <c>"failed"</c>. A missing section, field or an unknown value is
/// <see cref="BuildNotificationLevel.None"/>, which is also what the Hub's reset (deleting the section) gives.
/// </summary>
public static class BuildNotificationPreference
{
    /// <summary>The preferences section.</summary>
    public const string Section = "notifications";

    /// <summary>The field of the section.</summary>
    public const string BuildsField = "builds";

    private static readonly IReadOnlyDictionary<string, BuildNotificationLevel> Values =
        new Dictionary<string, BuildNotificationLevel>(StringComparer.Ordinal)
        {
            ["none"] = BuildNotificationLevel.None,
            ["all"] = BuildNotificationLevel.All,
            ["failed"] = BuildNotificationLevel.Failed
        };

    /// <summary>What <paramref name="user"/> chose.</summary>
    public static BuildNotificationLevel Of(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!user.Preferences.TryGetValue(Section, out var section))
            return BuildNotificationLevel.None;

        // Read back from the column it is a dictionary; a value set in this request may still be the JSON element
        var fields = section is JsonElement element ? PlainValue.From(element) : section;

        return fields is IDictionary<string, object?> map && map.TryGetValue(BuildsField, out var value) &&
               value is string text && Values.TryGetValue(text, out var level)
            ? level
            : BuildNotificationLevel.None;
    }

    /// <summary>Whether a user with <paramref name="level"/> is told about a build that ended with <paramref name="outcome"/>.</summary>
    public static bool Wants(this BuildNotificationLevel level, BuildOutcome outcome) =>
        level == BuildNotificationLevel.All || (level == BuildNotificationLevel.Failed && outcome == BuildOutcome.Failed);

    /// <summary>
    /// Checks the <see cref="Section"/> section of a preferences patch (not null: null deletes it): an object whose only
    /// field is <see cref="BuildsField"/>, with one of the known values.
    /// </summary>
    /// <exception cref="BadRequestException">The section is not valid; the message says why.</exception>
    public static void Validate(JsonElement section)
    {
        if (section.ValueKind != JsonValueKind.Object)
            throw new BadRequestException($"Section '{Section}' is an object");

        foreach (var field in section.EnumerateObject())
        {
            if (field.Name != BuildsField)
                throw new BadRequestException($"Section '{Section}' has no field '{field.Name}'; its field is '{BuildsField}'");

            if (field.Value.ValueKind != JsonValueKind.String || !Values.ContainsKey(field.Value.GetString()!))
                throw new BadRequestException(
                    $"'{Section}.{BuildsField}' is one of {string.Join(", ", Values.Keys.Select(k => $"\"{k}\""))}, not {field.Value.GetRawText()}");
        }
    }
}
