using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Registry.Common;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Users;

/// <summary>
/// The signed-in user's Hub preferences (<see cref="User.Preferences"/>): JSON sections with structural limits. The
/// server reads one section, <c>notifications</c> (which build e-mails the user wants, see
/// <see cref="BuildNotificationPreference"/>), and checks its shape when a patch sends it; every other section is
/// free-form. It writes that one column and never touches <see cref="User.Metadata"/> or the roles, so a section named
/// "roles" is just a section.
/// </summary>
public sealed partial class UserPreferences
{
    /// <summary>Most sections a user can hold.</summary>
    public const int MaxSections = 32;

    /// <summary>Largest stored form of all the sections (UTF-16 characters of the JSON text, a close bound in bytes).</summary>
    public const int MaxTotalBytes = 64 * 1024;

    /// <summary>Deepest nesting of objects and arrays in a section; the section value itself is level 1.</summary>
    public const int MaxDepth = 8;

    private readonly IAuthManager _authManager;
    private readonly ApplicationDbContext _db;

    public UserPreferences(IAuthManager authManager, ApplicationDbContext db)
    {
        _authManager = authManager;
        _db = db;
    }

    // \z, not $: a .NET $ also matches before a final newline, so "map\n" would pass as a name
    private const string SectionNameRule = @"^[a-z][a-zA-Z0-9]{0,31}\z";

    [GeneratedRegex(SectionNameRule)]
    private static partial Regex SectionNamePattern();

    /// <summary>All the sections of the current user ({} when none).</summary>
    public async Task<Dictionary<string, object>> Get() => (await CurrentUser()).Preferences;

    /// <summary>
    /// Sets the sections in <paramref name="patch"/> (null deletes one) and keeps the others; returns all the sections
    /// after the change. A violated limit throws before anything is written.
    /// </summary>
    public async Task<Dictionary<string, object>> Patch(Dictionary<string, JsonElement>? patch, CancellationToken ct)
    {
        if (patch == null)
            throw new BadRequestException("No preferences provided");

        ValidateShape(patch);

        // Tracked by the request's context (loaded through UserManager), so SaveChanges writes only the changed column
        // and leaves the ConcurrencyStamp alone: a preferences write must never make a login or an admin change fail
        var user = await CurrentUser();

        // A copy, assigned only when every limit holds: a refused patch leaves the tracked user as it was
        var merged = new Dictionary<string, object>(user.Preferences);
        foreach (var (section, value) in patch)
        {
            if (value.ValueKind == JsonValueKind.Null)
                merged.Remove(section);
            else
                merged[section] = PlainValue.From(value)!;
        }

        EnsureLimits(merged);

        user.Preferences = merged;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // The account row changed after it was loaded (a role change, a password change, a login)
            throw new ConflictException("The account changed while the preferences were saved; load them again", ex);
        }

        return merged;
    }

    private Task<User> CurrentUser() => _authManager.GetRequiredCurrentUser();

    private static void ValidateShape(Dictionary<string, JsonElement> patch)
    {
        foreach (var (section, value) in patch)
        {
            if (!SectionNamePattern().IsMatch(section))
                throw new BadRequestException(
                    $"A section name is a lowercase letter and at most 31 more letters or digits ({SectionNameRule})");

            if (CheckedDepth(value, section) > MaxDepth)
                throw new BadRequestException($"Section '{section}' is nested deeper than {MaxDepth} levels");

            // The section the server acts on: a bad value must be refused now, not silently read as "none" later
            if (section == BuildNotificationPreference.Section && value.ValueKind != JsonValueKind.Null)
                BuildNotificationPreference.Validate(value);
        }
    }

    /// <summary>
    /// Levels of objects and arrays in <paramref name="value"/>: 0 for a scalar, 1 for a flat object or array. Throws for
    /// a number that is neither a 64-bit integer nor a finite double (1e400 reads as infinity, which cannot be stored as
    /// JSON), in the same walk.
    /// </summary>
    private static int CheckedDepth(JsonElement value, string section)
    {
        var deepest = 0;
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                    deepest = int.Max(deepest, CheckedDepth(property.Value, section));
                return deepest + 1;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    deepest = int.Max(deepest, CheckedDepth(item, section));
                return deepest + 1;
            case JsonValueKind.Number:
                if (!value.TryGetInt64(out _) && !(value.TryGetDouble(out var number) && double.IsFinite(number)))
                    throw new BadRequestException(
                        $"Section '{section}' holds the number {value.GetRawText()}, outside the range of a double");
                return 0;
            default:
                return 0;
        }
    }

    private static void EnsureLimits(Dictionary<string, object> merged)
    {
        if (merged.Count > MaxSections)
            throw new BadRequestException($"At most {MaxSections} sections can be stored");

        if (UserMetadataJson.Serialize(merged).Length > MaxTotalBytes)
            throw new BadRequestException($"The stored preferences can be at most {MaxTotalBytes} bytes");
    }
}
