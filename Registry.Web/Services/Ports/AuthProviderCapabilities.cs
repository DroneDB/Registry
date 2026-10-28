namespace Registry.Web.Services.Ports;

/// <summary>
/// Describes what an authentication provider allows to manage locally.
/// Consumers (UsersManager, SystemController, bootstrap, health check) adapt their
/// behaviour to these flags without knowing the concrete provider.
/// </summary>
public sealed record AuthProviderCapabilities
{
    /// <summary>Users can be created and deleted locally (admin UI, API).</summary>
    public bool SupportsLocalUserManagement { get; init; }

    /// <summary>Passwords can be changed through Registry.</summary>
    public bool SupportsPasswordChange { get; init; }

    /// <summary>Roles are determined by the provider; local editing must be inhibited.</summary>
    public bool ManagesRolesExternally { get; init; }

    /// <summary>Email and display name are synchronised from the provider; local editing must be inhibited.</summary>
    public bool ManagesProfileExternally { get; init; }

    /// <summary>
    /// Capabilities for the local provider (full local control - historical behaviour).
    /// </summary>
    public static AuthProviderCapabilities Local { get; } = new()
    {
        SupportsLocalUserManagement = true,
        SupportsPasswordChange = true,
        ManagesRolesExternally = false,
        ManagesProfileExternally = false
    };

    /// <summary>
    /// Capabilities for external providers (Remote/LDAP): identity is managed outside Registry.
    /// </summary>
    public static AuthProviderCapabilities External { get; } = new()
    {
        SupportsLocalUserManagement = false,
        SupportsPasswordChange = false,
        ManagesRolesExternally = true,
        ManagesProfileExternally = true
    };
}
