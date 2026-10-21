#nullable enable
namespace Registry.Web.Models.Configuration;

/// <summary>Where users and passwords come from (keys of the <c>AppSettings</c> section).</summary>
public sealed class IdentityProviderOptions
{
    /// <summary>The administrator created on the first start (local accounts only).</summary>
    public AdminInfo? DefaultAdmin { get; set; }

    /// <summary>External authentication provider URL; mutually exclusive with <see cref="LdapSettings"/>.</summary>
    public string? ExternalAuthUrl { get; set; }

    /// <summary>LDAP/Active Directory authentication; mutually exclusive with <see cref="ExternalAuthUrl"/>.</summary>
    public LdapSettings? LdapSettings { get; set; }

    /// <summary>Password complexity policy. When null, no password requirements are enforced.</summary>
    public PasswordPolicy? PasswordPolicy { get; set; }

    /// <summary>True when LDAP authentication is enabled.</summary>
    public bool LdapEnabled => LdapSettings?.Enabled == true;

    /// <summary>True when an external authentication provider is configured.</summary>
    public bool ExternalAuthEnabled => !string.IsNullOrWhiteSpace(ExternalAuthUrl);
}
