#nullable disable
using System.Collections.Generic;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using static Registry.Web.Services.Configuration.ConfigurationFields;

namespace Registry.Web.Services.Configuration;

/// <summary>The admin configuration sections of authentication and identity: tokens, the default administrator, the password policy, LDAP.</summary>
public sealed class IdentitySections : IConfigurationSectionBuilder
{
    private readonly ConfigurationView _view;

    public IdentitySections(ConfigurationView view)
    {
        _view = view;
    }

    public IEnumerable<PlacedSection> Build() =>
    [
        new(ConfigurationSection.Auth, BuildAuthSection()),
        new(ConfigurationSection.DefaultAdmin, BuildDefaultAdminSection()),
        new(ConfigurationSection.PasswordPolicy, BuildPasswordPolicySection()),
        new(ConfigurationSection.Ldap, BuildLdapSection())
    ];

    private ConfigurationSectionDataDto BuildAuthSection()
    {
        var identity = _view.Configured<IdentityProviderOptions>();
        var identityDefault = _view.Shipped<IdentityProviderOptions>();
        var store = _view.Configured<DataStoreOptions>();
        var storeDefault = _view.Shipped<DataStoreOptions>();
        var token = _view.Configured<AuthTokenOptions>();
        var tokenDefault = _view.Shipped<AuthTokenOptions>();
        return Section("Auth", "Auth & Security",
            "JWT authentication, token expiration, and external auth configuration.",
            [
                SensitiveField("Secret", "JWT Secret",
                    "Secret key used to sign JWT tokens. Change this to invalidate all existing tokens.",
                    token.Secret, tokenDefault.Secret),

                NumberField("TokenExpirationInDays", "Token Expiration (days)",
                    "Number of days before a JWT token expires and requires re-authentication.",
                    token.TokenExpirationInDays, tokenDefault.TokenExpirationInDays,
                    minValue: 1, unit: "days"),

                EnumField("AuthProvider", "Auth Database Provider",
                    "Database provider for the Identity (auth) database. This controls the database backend, NOT the authentication mode.",
                    store.AuthProvider.ToString(), storeDefault.AuthProvider.ToString(),
                    ["Sqlite", "Mysql"]),

                StringField("ExternalAuthUrl", "External Auth URL",
                    "URL of an external authentication provider. When set, RemoteLoginManager is used instead of local login.",
                    identity.ExternalAuthUrl, identityDefault.ExternalAuthUrl),

                StringField("AuthCookieName", "Auth Cookie Name",
                    "Name of the HTTP cookie that stores the JWT token.",
                    token.AuthCookieName, tokenDefault.AuthCookieName),

                ArrayField("RevokedTokens", "Revoked Tokens",
                    "List of JWT token identifiers that have been explicitly revoked.",
                    token.RevokedTokens, tokenDefault.RevokedTokens)
            ]);
    }

    private ConfigurationSectionDataDto BuildDefaultAdminSection()
    {
        var identity = _view.Configured<IdentityProviderOptions>();
        var identityDefault = _view.Shipped<IdentityProviderOptions>();
        return Section("DefaultAdmin", "Default Admin",
            "Credentials for the default administrator account created on first run.",
            [
                StringField("Email", "Admin Email",
                    "Email address of the default admin user.",
                    identity.DefaultAdmin.Email, identityDefault.DefaultAdmin.Email),

                StringField("UserName", "Admin Username",
                    "Username of the default admin user.",
                    identity.DefaultAdmin.UserName, identityDefault.DefaultAdmin.UserName),

                SensitiveField("Password", "Admin Password",
                    "Password for the default admin account.",
                    identity.DefaultAdmin.Password, identityDefault.DefaultAdmin.Password)
            ]);
    }

    private ConfigurationSectionDataDto BuildPasswordPolicySection()
    {
        var identity = _view.Configured<IdentityProviderOptions>();
        var identityDefault = _view.Shipped<IdentityProviderOptions>();
        var pp = identity.PasswordPolicy;
        var dp = identityDefault.PasswordPolicy;
        return Section("PasswordPolicy", "Password Policy",
            "Password complexity requirements. When null in config, no policy is enforced.",
            [
                NumberField("MinLength", "Minimum Length",
                    "Minimum number of characters required for passwords.",
                    pp?.MinLength ?? 0, dp?.MinLength ?? 0, minValue: 1),

                BoolField("RequireDigit", "Require Digit",
                    "Password must contain at least one numeric digit.",
                    pp?.RequireDigit ?? false, dp?.RequireDigit ?? false),

                BoolField("RequireUppercase", "Require Uppercase",
                    "Password must contain at least one uppercase letter.",
                    pp?.RequireUppercase ?? false, dp?.RequireUppercase ?? false),

                BoolField("RequireLowercase", "Require Lowercase",
                    "Password must contain at least one lowercase letter.",
                    pp?.RequireLowercase ?? false, dp?.RequireLowercase ?? false),

                BoolField("RequireNonAlphanumeric", "Require Special Character",
                    "Password must contain at least one non-alphanumeric character.",
                    pp?.RequireNonAlphanumeric ?? false, dp?.RequireNonAlphanumeric ?? false)
            ]);
    }

    private ConfigurationSectionDataDto BuildLdapSection()
    {
        var identity = _view.Configured<IdentityProviderOptions>();
        var identityDefault = _view.Shipped<IdentityProviderOptions>();
        var ls = identity.LdapSettings;
        var dl = identityDefault.LdapSettings;
        var isActive = ls?.Enabled ?? false;

        var section = Section("LdapSettings", "LDAP / Active Directory",
            "LDAP authentication settings. Mutually exclusive with ExternalAuthUrl.",
            [
                BoolField("Enabled", "LDAP Enabled",
                    "Enables LDAP/Active Directory authentication. Mutually exclusive with ExternalAuthUrl.",
                    ls?.Enabled ?? false, dl?.Enabled ?? false),

                StringField("Server", "LDAP Server",
                    "LDAP server hostname (e.g., 'ldap.example.com' or 'dc.domain.com').",
                    ls?.Server, dl?.Server),

                NumberField("Port", "LDAP Port",
                    "LDAP port: 389 for plain LDAP, 636 for LDAPS.",
                    ls?.Port ?? 0, dl?.Port ?? 0),

                BoolField("UseSsl", "Use SSL/TLS",
                    "Use SSL/TLS (LDAPS) for the connection. Strongly recommended in production.",
                    ls?.UseSsl ?? false, dl?.UseSsl ?? false),

                BoolField("ValidateSslCertificate", "Validate SSL Certificate",
                    "Validate the server SSL certificate chain. Disable only for self-signed certs in testing.",
                    ls?.ValidateSslCertificate ?? false, dl?.ValidateSslCertificate ?? false),

                StringField("BaseDn", "Base DN",
                    "Base DN for searches (e.g., 'dc=example,dc=com').",
                    ls?.BaseDn, dl?.BaseDn),

                StringField("BindDn", "Bind DN",
                    "Service account DN for the initial search bind. Null for anonymous bind.",
                    ls?.BindDn, dl?.BindDn),

                SensitiveField("BindPassword", "Bind Password",
                    "Password for the Bind DN service account.",
                    ls?.BindPassword, dl?.BindPassword),

                StringField("UserDnFormat", "User DN Format",
                    "Optional format string for constructing user principal directly. {0} is replaced with username.",
                    ls?.UserDnFormat, dl?.UserDnFormat),

                StringField("EmailAttribute", "Email Attribute",
                    "LDAP attribute for the user email address. AD default: 'mail'.",
                    ls?.EmailAttribute, dl?.EmailAttribute),

                StringField("DisplayNameAttribute", "Display Name Attribute",
                    "LDAP attribute for the display name. AD default: 'displayName'.",
                    ls?.DisplayNameAttribute, dl?.DisplayNameAttribute),

                StringField("GroupMembershipAttribute", "Group Membership Attribute",
                    "LDAP attribute listing group memberships. Default: 'memberOf'.",
                    ls?.GroupMembershipAttribute, dl?.GroupMembershipAttribute),

                StringField("SearchFilter", "Search Filter",
                    "LDAP search filter to locate user entry. {0} is replaced with the username.",
                    ls?.SearchFilter, dl?.SearchFilter, "text"),

                ArrayField("AdminGroupDns", "Admin Group DN's",
                    "Distinguished names of LDAP groups whose members receive the Registry admin role.",
                    ls?.AdminGroupDns, dl?.AdminGroupDns),

                NumberField("Timeout", "LDAP Timeout",
                    "Timeout in seconds for LDAP operations.",
                    ls?.Timeout ?? 0, dl?.Timeout ?? 0, unit: "seconds")
            ]);

        // Override IsActive: LDAP section is active when Enabled == true
        section.IsActive = isActive;
        return section;
    }
}
