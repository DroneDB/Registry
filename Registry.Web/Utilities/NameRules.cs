namespace Registry.Web.Utilities;

/// <summary>
/// The one definition of a user or role name (FR-XC-12, D-007, D-015): lowercase letters a–z and digits 0–9, up to
/// <see cref="MaxLength"/> characters. It applies to users created in the Hub or through the API and to every role
/// that is created; the users an external provider signs in keep the wider Identity character set
/// (<c>AllowedUserNameCharacters</c>), which this rule does not touch. <c>GET /sys/features</c> publishes it
/// (<see cref="InputRules.Describe"/>, <see cref="InputRules.NamePattern"/>), so the Hub checks the same rule before it
/// submits. The members are those of <see cref="SlugRules"/>.
/// </summary>
public static class NameRules
{
    /// <summary>The shortest name.</summary>
    public const int MinLength = 1;

    /// <summary>The longest name (the user name column of Identity holds 256; the rule has always stopped at 127).</summary>
    public const int MaxLength = 127;

    /// <summary>The rule as a client reads it.</summary>
    public static readonly string Description =
        $"{MinLength} to {MaxLength} characters: lowercase letters (a–z) and digits (0–9)";

    /// <summary>The 400 message of a refused name of a <paramref name="kind"/> ("User name", "Role name").</summary>
    public static string InvalidMessage(string kind) => $"{kind} is invalid: use {Description}";

    /// <summary>The name has the length and characters of the rule (no uppercase letter, no sign, no space).</summary>
    public static bool IsValid(string? name)
    {
        if (name is null || name.Length < MinLength || name.Length > MaxLength)
            return false;

        foreach (var c in name)
            if (c is not (>= 'a' and <= 'z' or >= '0' and <= '9'))
                return false;

        return true;
    }
}
