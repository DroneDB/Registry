#nullable enable
namespace Registry.Web.Utilities;

/// <summary>
/// The one definition of an organization or dataset slug. Three places apply it: the validator
/// (<see cref="Extenders.IsValidSlug"/>), the slug generator (<see cref="Extenders.ToSlug"/>) and the route
/// constraint of the <c>{orgSlug}</c> and <c>{dsSlug}</c> segments (<see cref="SlugRouteConstraint"/>): a slug the
/// validator accepts must route (a slug stored with a length the routes refuse is unreachable), so they cannot
/// each have their own bounds.
/// <list type="bullet">
/// <item>The length is <see cref="MinLength"/> to <see cref="MaxLength"/> characters (the database column holds 128).</item>
/// <item>The characters are [a-z0-9_-], the first one [a-z0-9] (<see cref="IsWellFormed"/>: what the routes need).</item>
/// <item>No "--": <c>ToSlug</c> collapses dashes, and slugs stored before it did were repaired
/// (<see cref="Services.Maintenance.DatasetSlugRepair"/>). <see cref="IsValid"/> adds this to the form; a route
/// lets a slug with "--" through, so the request gets the validator's 400 and its message, not the SPA.</item>
/// </list>
/// </summary>
public static class SlugRules
{
    /// <summary>The shortest slug: what the routes have always needed.</summary>
    public const int MinLength = 2;

    /// <summary>The longest slug: the size of the slug columns.</summary>
    public const int MaxLength = 128;

    /// <summary>The rules as a client reads them.</summary>
    public static readonly string Description =
        $"it has {MinLength} to {MaxLength} characters (lowercase letters, digits, '_' and '-'), " +
        "starts with a letter or digit and has no '--'";

    /// <summary>The 400 message of a refused slug of an <paramref name="kind"/> ("Organization", "Dataset").</summary>
    public static string InvalidMessage(string kind) => $"{kind} slug is invalid: {Description}";

    /// <summary>The slug has the length and characters the routes need.</summary>
    public static bool IsWellFormed(string? slug)
    {
        if (slug is null || slug.Length < MinLength || slug.Length > MaxLength)
            return false;

        if (!IsLowerOrDigit(slug[0]))
            return false;

        foreach (var c in slug)
            if (!IsLowerOrDigit(c) && c is not ('_' or '-'))
                return false;

        return true;
    }

    /// <summary>The slug is well formed and has no consecutive dashes.</summary>
    public static bool IsValid(string? slug) => IsWellFormed(slug) && !slug!.Contains("--", System.StringComparison.Ordinal);

    private static bool IsLowerOrDigit(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';
}
