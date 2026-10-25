#nullable enable
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Test.Support;
using Registry.Web.Utilities.Auth;
using Shouldly;
using Registry.Test.Common;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// <c>GET sys/config</c> and <c>POST sys/check-artifact-completeness</c> had only the
/// class-level <c>[Authorize]</c>, so any logged-in user could read the configuration and start a
/// full rebuild scan. Both now require the <c>Admin</c> policy (the <c>admin</c> claim). The admin
/// claim is matched case-insensitively by one predicate (<see cref="Policies.IsAdmin"/>): the JWT
/// carries "true", <c>BasicAuthFilter</c> issues "True".
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~AdminPolicyTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public class AdminPolicyTests
{
    private const string ConfigUrl = "/sys/config";
    private const string CompletenessUrl = "/sys/check-artifact-completeness";
    private const string QuickHealthUrl = "/quickhealth";
    private const string RegularUser = "adminpolicy-regular-user";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        _client = _factory.CreateClient();
        await _factory.CreateUserAsync(RegularUser);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? user = null,
        bool isAdmin = false)
    {
        var request = new HttpRequestMessage(method, url);
        if (user != null)
            request.AsUser(user, isAdmin);
        return _client.SendAsync(request);
    }

    [Test]
    public async Task GetConfig_NonAdmin_IsForbidden()
    {
        using var response = await SendAsync(HttpMethod.Get, ConfigUrl, RegularUser);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task CheckArtifactCompleteness_NonAdmin_IsForbidden()
    {
        using var response = await SendAsync(HttpMethod.Post, CompletenessUrl, RegularUser);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task GetConfig_Anonymous_IsChallenged()
    {
        using var response = await SendAsync(HttpMethod.Get, ConfigUrl);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task CheckArtifactCompleteness_Anonymous_IsChallenged()
    {
        using var response = await SendAsync(HttpMethod.Post, CompletenessUrl);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task GetConfig_Admin_Succeeds()
    {
        using var response = await SendAsync(HttpMethod.Get, ConfigUrl, RegistryWebFactory.AdminUserName, true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task CheckArtifactCompleteness_Admin_OnlyEnqueuesTheScan()
    {
        using var response =
            await SendAsync(HttpMethod.Post, CompletenessUrl, RegistryWebFactory.AdminUserName, true);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // The factory runs Hangfire without servers: the scan is stored, never executed
        var body = JObject.Parse(await response.Content.ReadAsStringAsync());
        var jobId = (string?)(body["jobId"] ?? body["JobId"]);
        jobId.ShouldNotBeNullOrEmpty();

        using var connection = JobStorage.Current.GetConnection();
        var job = connection.GetJobData(jobId!);
        job.ShouldNotBeNull();
        job.Job.Type.ShouldBe(typeof(ArtifactCompletenessCheckerService));
        job.Job.Method.Name.ShouldBe(nameof(ArtifactCompletenessCheckerService.CheckAndQueueAsync));
    }

    // The predicate itself: the claim values every authentication path issues

    private static ClaimsPrincipal Principal(string? adminClaimValue, bool authenticated = true)
    {
        var claims = adminClaimValue == null
            ? new[] { new Claim(ClaimTypes.Name, "user-id") }
            : new[] { new Claim(ClaimTypes.Name, "user-id"), new Claim("admin", adminClaimValue) };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "test" : null));
    }

    [TestCase("true")]   // JWT
    [TestCase("True")]   // BasicAuthFilter (bool.ToString())
    [TestCase("TRUE")]
    public void IsAdmin_TrueClaim_AnyCase_IsAccepted(string value) =>
        Policies.IsAdmin(Principal(value)).ShouldBeTrue();

    [TestCase("false")]
    [TestCase("False")]
    [TestCase("")]
    [TestCase("yes")]
    public void IsAdmin_NonTrueClaim_IsRejected(string value) =>
        Policies.IsAdmin(Principal(value)).ShouldBeFalse();

    [Test]
    public void IsAdmin_NoClaim_IsRejected() => Policies.IsAdmin(Principal(null)).ShouldBeFalse();

    [Test]
    public void IsAdmin_Unauthenticated_IsRejected() =>
        Policies.IsAdmin(Principal("true", authenticated: false)).ShouldBeFalse();

    [Test]
    public void IsAdmin_NullPrincipal_IsRejected() => Policies.IsAdmin(null).ShouldBeFalse();
}

/// <summary>
/// An admin authenticated with the claim value "True" (what
/// <c>BasicAuthFilter</c> issues) is an admin for every admin check, here the <c>Admin</c> policy
/// (<c>sys/config</c>) and <c>RequireAdminOrMonitorToken</c> (<c>/quickhealth</c>), which used to
/// compare the value with "true" case-sensitively.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~TitleCaseAdminClaimTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public class TitleCaseAdminClaimTests
{
    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    /// <summary>Authenticates every request as the default admin with the admin claim "True".</summary>
    private sealed class TitleCaseAdminHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TitleCaseAdmin";

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var userManager = Context.RequestServices.GetRequiredService<UserManager<User>>();
            var user = await userManager.FindByNameAsync(RegistryWebFactory.AdminUserName);
            if (user == null)
                return AuthenticateResult.Fail("Default admin missing");

            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, user.Id),
                new Claim(ApplicationDbContext.AdminRoleName.ToLowerInvariant(), true.ToString())
            ], SchemeName);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
        }
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new RegistryWebFactory(configureServices: services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TitleCaseAdminHandler>(TitleCaseAdminHandler.SchemeName, null);
            // Registered after the factory's own PostConfigure: this one wins
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = TitleCaseAdminHandler.SchemeName;
                options.DefaultAuthenticateScheme = TitleCaseAdminHandler.SchemeName;
                options.DefaultChallengeScheme = TitleCaseAdminHandler.SchemeName;
            });
        });
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task QuickHealth_TitleCaseAdminClaim_IsAdmin()
    {
        using var response = await _client.GetAsync("/quickhealth");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task GetConfig_TitleCaseAdminClaim_IsAdmin()
    {
        using var response = await _client.GetAsync("/sys/config");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
