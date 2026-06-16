using System;

namespace Registry.Web.Models.Configuration;

public class LdapSettings
{
    /// <summary>Enables LDAP/Active Directory authentication.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>LDAP server host (e.g. "ldap.example.com" or "dc.domain.com").</summary>
    public string Server { get; set; }

    /// <summary>LDAP port. 389 for plain LDAP, 636 for LDAPS.</summary>
    public int Port { get; set; } = 636;

    /// <summary>Use SSL/TLS (LDAPS). Strongly recommended in production.</summary>
    public bool UseSsl { get; set; } = true;

    /// <summary>
    /// Validate the server SSL certificate chain.
    /// Set to false only for local testing with self-signed certificates.
    /// </summary>
    public bool ValidateSslCertificate { get; set; } = true;

    /// <summary>
    /// Base DN for searches (e.g. "dc=example,dc=com").
    /// Required.
    /// </summary>
    public string BaseDn { get; set; }

    /// <summary>
    /// Service account DN used for the initial search bind.
    /// If null, an anonymous bind is attempted.
    /// </summary>
    public string BindDn { get; set; }

    /// <summary>
    /// Password for <see cref="BindDn"/>.
    /// Never commit in plain text - use environment variables or secrets management.
    /// </summary>
    public string BindPassword { get; set; }

    /// <summary>
    /// LDAP search filter to locate the user entry.
    /// <c>{0}</c> is replaced with the (escaped) username.
    /// AD default: <c>(sAMAccountName={0})</c>.
    /// OpenLDAP default: <c>(uid={0})</c>.
    /// </summary>
    public string SearchFilter { get; set; } = "(sAMAccountName={0})";

    /// <summary>
    /// Optional format string for constructing the user principal directly (bypasses the search step).
    /// <c>{0}</c> is replaced with the username.
    /// Examples: <c>{0}@domain.com</c> (UPN) or <c>CN={0},OU=Users,DC=domain,DC=com</c>.
    /// </summary>
    public string UserDnFormat { get; set; }

    /// <summary>
    /// Distinguished names of LDAP groups whose members receive the Registry admin role.
    /// Comparison is case-insensitive.
    /// </summary>
    public string[] AdminGroupDns { get; set; } = Array.Empty<string>();

    /// <summary>LDAP attribute for the user email address. AD default: <c>mail</c>.</summary>
    public string EmailAttribute { get; set; } = "mail";

    /// <summary>LDAP attribute for the display name. AD default: <c>displayName</c>.</summary>
    public string DisplayNameAttribute { get; set; } = "displayName";

    /// <summary>LDAP attribute listing group memberships. Default: <c>memberOf</c>.</summary>
    public string GroupMembershipAttribute { get; set; } = "memberOf";

    /// <summary>Timeout in seconds for LDAP operations.</summary>
    public int Timeout { get; set; } = 30;
}
