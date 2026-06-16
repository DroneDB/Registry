using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Shouldly;
using Registry.Web.HealthChecks;
using Registry.Web.Identity;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Managers;

namespace Registry.Web.Test;

/// <summary>
/// End-to-end tests for <see cref="LdapLoginManager"/> and <see cref="LdapHealthCheck"/> that run
/// against a real LDAP server (glauth) started in a throw-away Docker container via Testcontainers.
///
/// Marked <see cref="ExplicitAttribute"/> because it requires a working Docker daemon and pulls the
/// <c>glauth/glauth</c> image; it is therefore excluded from the default unit-test run and is intended
/// to be executed deliberately (locally or in a Docker-enabled CI job).
///
/// glauth quirks relied upon here (verified against glauth v2.5.0):
///  - User DN returned by a search is <c>cn=&lt;name&gt;,ou=&lt;primarygroup&gt;,ou=users,&lt;baseDN&gt;</c>.
///  - <c>memberOf</c> values use the form <c>ou=&lt;group&gt;,ou=groups,&lt;baseDN&gt;</c>.
///  - A bound user can only read attributes if granted a <c>search</c> capability; the service-account
///    search path (BindDn) does not require the end user to have that capability.
///  - glauth <b>accepts</b> anonymous binds, so the health check's "Degraded" branch (anonymous bind
///    rejected by policy, as Active Directory does) cannot be reproduced here and is not asserted.
/// </summary>
[TestFixture]
[Category("Integration")]
[Explicit("Requires Docker: starts a glauth LDAP server in a container.")]
public class LdapLoginManagerIntegrationTest
{
    private const string GlauthImage = "glauth/glauth:v2.5.0";
    private const ushort LdapPort = 3893;

    private const string BaseDn = "dc=glauth,dc=com";
    private const string ServiceUserDn = "cn=serviceuser,ou=svcaccts,dc=glauth,dc=com";
    private const string AdminGroupDn = "ou=registry-admins,ou=groups,dc=glauth,dc=com";

    private const string ServicePass = "ServiceP@ss1";
    private const string UserPass = "UserP@ss1";
    private const string AdminPass = "AdminP@ss1";

    private IContainer _container;
    private string _host;
    private ushort _port;

    [OneTimeSetUp]
    public async Task StartLdapServer()
    {
        _container = new ContainerBuilder(GlauthImage)
            .WithResourceMapping(Encoding.UTF8.GetBytes(BuildGlauthConfig()), "/app/config/config.cfg")
            .WithPortBinding(LdapPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("LDAP server listening"))
            .Build();

        await _container.StartAsync();

        _host = _container.Hostname;
        _port = _container.GetMappedPublicPort(LdapPort);
    }

    [OneTimeTearDown]
    public async Task StopLdapServer()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    // -------------------------------------------------------------------------
    // Authentication (service-account search path)
    // -------------------------------------------------------------------------

    [Test]
    public async Task CheckAccess_ValidUser_ReturnsSuccessWithMappedMetadata()
    {
        var manager = CreateManager(BaseSettings());

        var result = await manager.CheckAccess("jdoe", UserPass);

        result.Success.ShouldBeTrue();
        result.UserName.ShouldBe("jdoe");
        result.Metadata["email"].ShouldBe("jdoe@example.com");
        result.Metadata["displayName"].ShouldBe("John");
        result.Metadata["authProvider"].ShouldBe("ldap");
        ((string[])result.Metadata["roles"]).ShouldBeEmpty();
    }

    [Test]
    public async Task CheckAccess_AdminGroupMember_ReceivesAdminRole()
    {
        var manager = CreateManager(BaseSettings());

        var result = await manager.CheckAccess("alice", AdminPass);

        result.Success.ShouldBeTrue();
        ((string[])result.Metadata["roles"]).ShouldContain(ApplicationDbContext.AdminRoleName);
    }

    [Test]
    public async Task CheckAccess_WrongPassword_Fails()
    {
        var manager = CreateManager(BaseSettings());

        var result = await manager.CheckAccess("jdoe", "definitely-wrong");

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task CheckAccess_UnknownUser_Fails()
    {
        var manager = CreateManager(BaseSettings());

        var result = await manager.CheckAccess("ghost", "whatever");

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task CheckAccess_EmptyCredentials_Fails()
    {
        var manager = CreateManager(BaseSettings());

        (await manager.CheckAccess("", "")).Success.ShouldBeFalse();
        (await manager.CheckAccess("jdoe", "")).Success.ShouldBeFalse();
    }

    [Test]
    public async Task CheckAccess_LdapFilterInjection_Fails()
    {
        var manager = CreateManager(BaseSettings());

        // A wildcard or filter-injection username must never authenticate: EscapeLdapFilter neutralises
        // the special characters so the search matches nothing.
        (await manager.CheckAccess("*", "whatever")).Success.ShouldBeFalse();
        (await manager.CheckAccess("jdoe)(uid=*", UserPass)).Success.ShouldBeFalse();
    }

    [Test]
    public async Task CheckAccess_Token_NotSupported()
    {
        var manager = CreateManager(BaseSettings());

        (await manager.CheckAccess("any-token")).Success.ShouldBeFalse();
    }

    // -------------------------------------------------------------------------
    // Authentication (direct-bind / UserDnFormat path, no service account)
    // -------------------------------------------------------------------------

    [Test]
    public async Task CheckAccess_UserDnFormatDirectBind_Succeeds()
    {
        var settings = BaseSettings();
        settings.BindDn = null;
        settings.BindPassword = null;
        settings.UserDnFormat = "cn={0},ou=users,dc=glauth,dc=com";

        var manager = CreateManager(settings);

        var result = await manager.CheckAccess("jdoe", UserPass);

        result.Success.ShouldBeTrue();
        result.Metadata["email"].ShouldBe("jdoe@example.com");
    }

    [Test]
    public async Task CheckAccess_UserDnFormatDirectBind_WrongPassword_Fails()
    {
        var settings = BaseSettings();
        settings.BindDn = null;
        settings.BindPassword = null;
        settings.UserDnFormat = "cn={0},ou=users,dc=glauth,dc=com";

        var manager = CreateManager(settings);

        (await manager.CheckAccess("jdoe", "definitely-wrong")).Success.ShouldBeFalse();
    }

    // -------------------------------------------------------------------------
    // Health check
    // -------------------------------------------------------------------------

    [Test]
    public async Task LdapHealthCheck_WithServiceBind_IsHealthy()
    {
        var healthCheck = new LdapHealthCheck(Wrap(BaseSettings()));

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Test]
    public async Task LdapHealthCheck_UnreachableServer_IsUnhealthy()
    {
        var settings = BaseSettings();
        settings.Port = 1; // nothing is listening here
        settings.Timeout = 3;

        var healthCheck = new LdapHealthCheck(Wrap(settings));

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.ShouldBe(HealthStatus.Unhealthy);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private LdapSettings BaseSettings() => new()
    {
        Enabled = true,
        Server = _host,
        Port = _port,
        UseSsl = false,
        ValidateSslCertificate = false,
        BaseDn = BaseDn,
        BindDn = ServiceUserDn,
        BindPassword = ServicePass,
        SearchFilter = "(cn={0})",
        UserDnFormat = null,
        AdminGroupDns = new[] { AdminGroupDn },
        EmailAttribute = "mail",
        DisplayNameAttribute = "givenName",
        GroupMembershipAttribute = "memberOf",
        Timeout = 15
    };

    private static LdapLoginManager CreateManager(LdapSettings settings) =>
        new(NullLogger<LdapLoginManager>.Instance, Wrap(settings));

    private static IOptions<AppSettings> Wrap(LdapSettings settings) =>
        Microsoft.Extensions.Options.Options.Create(new AppSettings { LdapSettings = settings });

    /// <summary>
    /// Builds a minimal glauth "config" datastore. Passwords are stored as their SHA-256 hashes
    /// (glauth's <c>passsha256</c>) computed here so the plaintext stays in one place.
    /// <para/>
    /// Group layout: <c>users</c> (5501), <c>registry-admins</c> (5502), <c>svcaccts</c> (5503).
    /// - <c>serviceuser</c> binds and searches (has the <c>search</c> capability).
    /// - <c>jdoe</c> is a normal user (primary group <c>users</c>); it also has a <c>search</c>
    ///   capability so the direct-bind / UserDnFormat path can read its own attributes.
    /// - <c>alice</c> is an admin: primary group <c>users</c> plus <c>registry-admins</c> via
    ///   <c>othergroups</c>, producing the <c>memberOf</c> entry matched by <see cref="AdminGroupDn"/>.
    /// </summary>
    private static string BuildGlauthConfig() => $$"""
        [ldap]
          enabled = true
          listen = "0.0.0.0:3893"
        [ldaps]
          enabled = false
        [backend]
          datastore = "config"
          baseDN = "dc=glauth,dc=com"
        [[users]]
          name = "serviceuser"
          uidnumber = 5003
          primarygroup = 5503
          passsha256 = "{{Sha256Hex(ServicePass)}}"
            [[users.capabilities]]
            action = "search"
            object = "*"
        [[users]]
          name = "jdoe"
          givenname = "John"
          sn = "Doe"
          mail = "jdoe@example.com"
          uidnumber = 5002
          primarygroup = 5501
          passsha256 = "{{Sha256Hex(UserPass)}}"
            [[users.capabilities]]
            action = "search"
            object = "*"
        [[users]]
          name = "alice"
          givenname = "Alice"
          sn = "Admin"
          mail = "alice@example.com"
          uidnumber = 5004
          primarygroup = 5501
          othergroups = [ 5502 ]
          passsha256 = "{{Sha256Hex(AdminPass)}}"
        [[groups]]
          name = "users"
          gidnumber = 5501
        [[groups]]
          name = "registry-admins"
          gidnumber = 5502
        [[groups]]
          name = "svcaccts"
          gidnumber = 5503
        """;

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
