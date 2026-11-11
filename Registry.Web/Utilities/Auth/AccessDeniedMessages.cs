using System;
using Registry.Web.Services.Managers;

namespace Registry.Web.Utilities.Auth;

/// <summary>
/// The client-facing message of every refused access check: one per rule, so a client sees the
/// same text for the same refusal whatever the endpoint. The status is 401 for
/// an anonymous caller and 403 for a signed-in one.
/// </summary>
public static class AccessDeniedMessages
{
    /// <summary>Message of the <c>Admin</c> policy and of admin-only business rules.</summary>
    public const string AdminRequired = "Only administrators can perform this operation";

    /// <summary>Message of a 401 of the authorization middleware: no valid credentials (none, or an expired token).</summary>
    public const string NotSignedIn = "Not signed in";

    /// <summary>Message of a 403 of the authorization middleware for a rule that has no message of its own.</summary>
    public const string OperationRefused = "The current user is not allowed to perform this operation";

    /// <summary>The refusal of <paramref name="access"/> to a dataset.</summary>
    public static string Dataset(AccessType access) => $"The current user is not allowed to {Verb(access)} this dataset";

    /// <summary>The refusal of <paramref name="access"/> to an organization.</summary>
    public static string Organization(AccessType access) =>
        $"The current user is not allowed to {Verb(access)} this organization";

    private static string Verb(AccessType access) => access switch
    {
        AccessType.Read => "read",
        AccessType.Write => "write to",
        AccessType.Delete => "delete",
        _ => throw new ArgumentOutOfRangeException(nameof(access), access, null)
    };
}
