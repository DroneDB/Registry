using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Identity;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Authenticates users against an LDAP or Active Directory server.
/// Implements the <see cref="ILoginManager"/> contract; all Registry authorization
/// and user-lifecycle concerns are handled upstream in <c>UsersManager</c>.
/// </summary>
public class LdapLoginManager : ILoginManager
{
    private readonly ILogger<LdapLoginManager> _logger;
    private readonly LdapSettings _settings;

    public LdapLoginManager(ILogger<LdapLoginManager> logger, IOptions<AppSettings> appSettings)
    {
        _logger = logger;
        _settings = appSettings.Value.LdapSettings
            ?? throw new InvalidOperationException(
                "LdapSettings must be configured in appsettings.json when LDAP authentication is enabled.");
    }

    /// <inheritdoc />
    public AuthProviderCapabilities Capabilities => AuthProviderCapabilities.External;

    /// <inheritdoc />
    public Task<LoginResultDto> CheckAccess(string token)
    {
        // LDAP does not support token-based authentication
        return Task.FromResult(new LoginResultDto { Success = false });
    }

    /// <inheritdoc />
    public Task<LoginResultDto> CheckAccess(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
            return Task.FromResult(new LoginResultDto { Success = false, UserName = userName });

        var safeUserName = EscapeLdapFilter(userName);

        try
        {
            string userEmail;
            string displayName;
            List<string> memberOf;

            if (!string.IsNullOrWhiteSpace(_settings.UserDnFormat))
            {
                // Path A: direct bind using UserDnFormat (e.g. UPN: "{0}@domain.com")
                // The username is not DN-escaped here; UPN and similar formats do not use DN syntax.
                var userPrincipal = string.Format(_settings.UserDnFormat, userName);

                using var conn = CreateConnection();
                conn.Bind(new NetworkCredential(userPrincipal, password));

                // Retrieve attributes via the authenticated user's own credentials
                (userEmail, displayName, memberOf) = FetchUserAttributes(conn, safeUserName);
            }
            else
            {
                // Path B: service account search to locate the user DN, then user bind to verify password
                string userDn;

                using (var svcConn = CreateConnection())
                {
                    if (!string.IsNullOrWhiteSpace(_settings.BindDn))
                        svcConn.Bind(new NetworkCredential(_settings.BindDn, _settings.BindPassword));
                    else
                        svcConn.Bind(); // anonymous bind

                    (userDn, userEmail, displayName, memberOf) = SearchUser(svcConn, safeUserName);
                }

                if (userDn == null)
                {
                    _logger.LogInformation(
                        "LDAP authentication failed: user {UserName} not found in directory", userName);
                    return Task.FromResult(new LoginResultDto { Success = false, UserName = userName });
                }

                // Verify the user's password by attempting a bind as the user
                using var userConn = CreateConnection();
                userConn.Bind(new NetworkCredential(userDn, password));
            }

            var isAdmin = IsAdminGroupMember(memberOf);
            var roles = isAdmin
                ? new[] { ApplicationDbContext.AdminRoleName }
                : Array.Empty<string>();

            _logger.LogInformation(
                "LDAP authentication successful for user {UserName} (admin: {IsAdmin})", userName, isAdmin);

            return Task.FromResult(new LoginResultDto
            {
                Success = true,
                UserName = userName,
                Metadata = new Dictionary<string, object>
                {
                    ["email"] = userEmail ?? string.Empty,
                    ["displayName"] = displayName ?? userName,
                    ["roles"] = roles,
                    ["authProvider"] = "ldap"
                }
            });
        }
        catch (LdapException ex) when (ex.ErrorCode == 49) // InvalidCredentials
        {
            _logger.LogInformation(
                "LDAP authentication failed for user {UserName}: invalid credentials", userName);
            return Task.FromResult(new LoginResultDto { Success = false, UserName = userName });
        }
        catch (LdapException ex)
        {
            _logger.LogError(ex,
                "LDAP error (code {ErrorCode}) during authentication for user {UserName}", ex.ErrorCode, userName);
            return Task.FromResult(new LoginResultDto { Success = false, UserName = userName });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during LDAP authentication for user {UserName}", userName);
            return Task.FromResult(new LoginResultDto { Success = false, UserName = userName });
        }
    }

    /// <summary>Creates a configured <see cref="LdapConnection"/> for this provider's settings.</summary>
    internal LdapConnection CreateConnection()
    {
        var identifier = new LdapDirectoryIdentifier(_settings.Server, _settings.Port, false, false);
        var conn = new LdapConnection(identifier)
        {
            AuthType = AuthType.Basic,
            Timeout = TimeSpan.FromSeconds(_settings.Timeout)
        };

        conn.SessionOptions.ProtocolVersion = 3;

        if (_settings.UseSsl)
        {
            conn.SessionOptions.SecureSocketLayer = true;

            if (!_settings.ValidateSslCertificate)
                conn.SessionOptions.VerifyServerCertificate = (_, _) => true;
        }

        return conn;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private (string dn, string email, string displayName, List<string> groups) SearchUser(
        LdapConnection conn, string safeUserName)
    {
        var filter = string.Format(_settings.SearchFilter, safeUserName);
        var attrs = new[]
        {
            _settings.EmailAttribute,
            _settings.DisplayNameAttribute,
            _settings.GroupMembershipAttribute
        };

        var request = new SearchRequest(_settings.BaseDn, filter, SearchScope.Subtree, attrs)
        {
            SizeLimit = 1,
            TimeLimit = TimeSpan.FromSeconds(_settings.Timeout)
        };

        var response = (SearchResponse)conn.SendRequest(request);

        if (response.Entries.Count == 0)
            return (null, null, null, []);

        var entry = response.Entries[0];
        return (
            entry.DistinguishedName,
            GetAttribute(entry, _settings.EmailAttribute),
            GetAttribute(entry, _settings.DisplayNameAttribute),
            GetMultiValueAttribute(entry, _settings.GroupMembershipAttribute)
        );
    }

    private (string email, string displayName, List<string> groups) FetchUserAttributes(
        LdapConnection conn, string safeUserName)
    {
        var (_, email, displayName, groups) = SearchUser(conn, safeUserName);
        return (email, displayName, groups);
    }

    private bool IsAdminGroupMember(List<string> memberOf)
    {
        if (_settings.AdminGroupDns == null || _settings.AdminGroupDns.Length == 0 || memberOf == null)
            return false;

        foreach (var group in memberOf)
            foreach (var adminDn in _settings.AdminGroupDns)
                if (string.Equals(group, adminDn, StringComparison.OrdinalIgnoreCase))
                    return true;

        return false;
    }

    private static string GetAttribute(SearchResultEntry entry, string attributeName)
    {
        if (string.IsNullOrEmpty(attributeName) || !entry.Attributes.Contains(attributeName))
            return null;

        var values = entry.Attributes[attributeName].GetValues(typeof(string));
        return values.Length > 0 ? values[0] as string : null;
    }

    private static List<string> GetMultiValueAttribute(SearchResultEntry entry, string attributeName)
    {
        if (string.IsNullOrEmpty(attributeName) || !entry.Attributes.Contains(attributeName))
            return [];

        var values = entry.Attributes[attributeName].GetValues(typeof(string));
        var result = new List<string>(values.Length);
        foreach (var v in values)
            if (v is string s)
                result.Add(s);
        return result;
    }

    /// <summary>
    /// Escapes special characters in an LDAP search filter value per RFC 4515.
    /// Must be applied to any user-supplied string inserted into a filter expression.
    /// </summary>
    private static string EscapeLdapFilter(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        return input
            .Replace("\\", "\\5c")
            .Replace("*", "\\2a")
            .Replace("(", "\\28")
            .Replace(")", "\\29")
            .Replace("\0", "\\00");
    }
}
