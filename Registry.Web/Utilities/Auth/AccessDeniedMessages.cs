#nullable enable
using System;
using Registry.Web.Services.Managers;

namespace Registry.Web.Utilities.Auth;

/// <summary>
/// The client-facing message of every refused access check (RQ-06): one per rule, so a client sees the
/// same text for the same refusal whatever the endpoint. The status comes from plan decision D2 (401 for
/// an anonymous caller, 403 for a signed-in one).
/// </summary>
public static class AccessDeniedMessages
{
    /// <summary>Message of the <c>Admin</c> policy and of admin-only business rules.</summary>
    public const string AdminRequired = "Only administrators can perform this operation";

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
