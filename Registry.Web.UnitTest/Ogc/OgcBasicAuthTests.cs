#nullable enable
using Registry.Web.Test.Support;
using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Web.Controllers;
using Registry.Web.Controllers.Ogc;
using Registry.Web.Data.Models;
using Registry.Web.Filters;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Ports;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities.Ogc;
using Shouldly;

namespace Registry.Web.Test.Ogc;

/// <summary>
/// HTTP Basic credentials must authenticate a request before the OGC access check: OGC clients
/// (QGIS, ArcGIS) send Basic auth to private datasets. The tests run the authorization filters
/// of each controller as MVC does (sorted by Order, then scope, then declaration).
/// </summary>
[TestFixture]
public class OgcBasicAuthTests
{
    private const string Org = "org";
    private const string Ds = "ds";
    private const string UserId = "user-1";

    private Dataset _dataset = null!;
    private ServiceProvider _services = null!;
    private DefaultHttpContext _http = null!;
    private Mock<IAuthManager> _auth = null!;

    [SetUp]
    public void SetUp()
    {
        _dataset = new Dataset { Slug = Ds, InternalRef = Guid.NewGuid() };
        _http = new DefaultHttpContext();

        var user = new User { Id = UserId, UserName = "admin" };
        // The Basic credentials are checked by the sign-in service (RQ-12: IUserSessions, out of IUsersManager)
        var usersManager = new Mock<Registry.Web.Services.Users.IUserSessions>();
        usersManager.Setup(m => m.Authenticate("admin", "password123"))
            .ReturnsAsync(new AuthenticateResponse(user, "jwt", DateTime.UtcNow.AddHours(1)));
        var userManager = new Mock<UserManager<User>>(
            new Mock<IUserStore<User>>().Object, null!, null!, null!, null!, null!, null!, null!, null!);
        userManager.Setup(m => m.FindByNameAsync("admin")).ReturnsAsync(user);
        userManager.Setup(m => m.IsInRoleAsync(user, It.IsAny<string>())).ReturnsAsync(false);

        var utils = new Mock<IDatasetResolver>();
        utils.Setup(u => u.GetDatasetAsync(Org, Ds, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(_dataset);

        // A private dataset: readable by the authenticated user only, as AuthManager decides
        // from the request's user
        var auth = _auth = new Mock<IAuthManager>();
        auth.Setup(a => a.RequestAccess(_dataset, AccessType.Read)).ReturnsAsync(() =>
            _http.User.FindFirst(ClaimTypes.Name)?.Value == UserId);

        _services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(usersManager.Object)
            .AddSingleton(userManager.Object)
            .AddSingleton(Microsoft.Extensions.Options.Options.Create(new AuthTokenOptions { AuthCookieName = "jwtToken" }))
            .AddSingleton(utils.Object)
            .AddSingleton(auth.Object)
            .AddSingleton(Mock.Of<IDdbManager>())
            .AddScoped<IDatasetAccessResolver, DatasetAccessResolver>()
            .AddSingleton(TestPaths.Policy)
            .AddScoped<BasicAuthFilter>()
            .AddScoped<OgcAuthorizationFilter>()
            .BuildServiceProvider();
        _http.RequestServices = _services;
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    private static IEnumerable<Type> OgcControllers() =>
    [
        typeof(WmsController), typeof(WmtsController), typeof(WcsController), typeof(WfsController),
        typeof(OgcApiController), typeof(MvtController)
    ];

    /// <summary>
    /// Runs the authorization filters declared on <paramref name="controller"/> in MVC order and
    /// returns the short-circuit result, if any.
    /// </summary>
    private async Task<IActionResult?> RunAuthorizationStage(Type controller)
    {
        var routeData = new RouteData();
        routeData.Values["orgSlug"] = Org;
        routeData.Values["dsSlug"] = Ds;
        var context = new AuthorizationFilterContext(new ActionContext(_http, routeData, new ActionDescriptor()),
            new List<IFilterMetadata>());

        var ordered = controller.GetCustomAttributes(true).OfType<IFilterMetadata>()
            .Select((f, i) => (Descriptor: new FilterDescriptor(f, FilterScope.Controller), Index: i))
            .OrderBy(d => d.Descriptor.Order).ThenBy(d => d.Descriptor.Scope).ThenBy(d => d.Index)
            .Select(d => d.Descriptor.Filter);

        foreach (var metadata in ordered)
        {
            var filter = metadata is IFilterFactory factory ? factory.CreateInstance(_services) : metadata;
            if (filter is IAsyncAuthorizationFilter asyncFilter)
                await asyncFilter.OnAuthorizationAsync(context);
            else if (filter is IAuthorizationFilter syncFilter)
                syncFilter.OnAuthorization(context);
            else
                continue;

            if (context.Result != null)
                return context.Result;
        }

        return null;
    }

    private void SendBasic(string user, string password) =>
        _http.Request.Headers.Authorization =
            "Basic " + Convert.ToBase64String(Encoding.Latin1.GetBytes($"{user}:{password}"));

    [TestCaseSource(nameof(OgcControllers))]
    public async Task BasicCredentials_ReadPrivateDataset(Type controller)
    {
        SendBasic("admin", "password123");

        var result = await RunAuthorizationStage(controller);

        result.ShouldBeNull("valid Basic credentials must pass the OGC access check");
        _http.User.FindFirst(ClaimTypes.Name)?.Value.ShouldBe(UserId);
        // The managers of the request reuse the filter's grant (the scoped resolver): no second check
        var access = await _services.GetRequiredService<IDatasetAccessResolver>().ResolveAsync(Org, Ds, AccessType.Read);
        access.Dataset.ShouldBe(_dataset);
        _auth.Verify(a => a.RequestAccess(_dataset, AccessType.Read), Times.Once);
    }

    [TestCaseSource(nameof(OgcControllers))]
    public async Task WrongCredentials_Get401WithChallenge(Type controller)
    {
        SendBasic("admin", "wrong");

        var result = await RunAuthorizationStage(controller);

        result.ShouldBeOfType<ContentResult>().StatusCode.ShouldBe(401);
        _http.Response.Headers.WWWAuthenticate.ToString().ShouldStartWith("Basic");
    }

    [TestCaseSource(nameof(OgcControllers))]
    public async Task NoCredentials_Get401WithChallenge(Type controller)
    {
        var result = await RunAuthorizationStage(controller);

        result.ShouldBeOfType<ContentResult>().StatusCode.ShouldBe(401);
        _http.Response.Headers.WWWAuthenticate.ToString().ShouldStartWith("Basic");
    }

    [Test]
    public async Task MalformedBasicHeader_IsAnonymous()
    {
        _http.Request.Headers.Authorization = "Basic not-base64!";

        var result = await RunAuthorizationStage(typeof(WmsController));

        result.ShouldBeOfType<ContentResult>().StatusCode.ShouldBe(401);
    }

    [TestCase(null, false, "", "")]
    [TestCase("Bearer abc", false, "", "")]
    [TestCase("Basic not-base64!", false, "", "")]
    [TestCase("Basic dXNlcg==", false, "", "")]                 // "user": no separator
    [TestCase("Basic dXNlcjpwYTpzcw==", true, "user", "pa:ss")] // password with a colon
    [TestCase("basic dXNlcjo=", true, "user", "")]              // scheme is case-insensitive
    public void TryReadCredentials_ParsesBasicHeaders(string? header, bool ok, string user, string password)
    {
        BasicAuthFilter.TryReadCredentials(header, out var u, out var p).ShouldBe(ok);
        u.ShouldBe(user);
        p.ShouldBe(password);
    }

    /// <summary>
    /// Every endpoint that accepts Basic credentials authenticates them before any other
    /// authorization filter or the action runs.
    /// </summary>
    [Test]
    public void BasicAuthentication_RunsFirstWhereverItIsDeclared()
    {
        var targets = typeof(BasicAuthFilter).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => new MemberInfo[] { t }.Concat(t.GetMethods()))
            .Select(m => m.GetCustomAttributes(true).OfType<IFilterMetadata>().ToList())
            .Where(filters => filters.Any(IsBasicAuthentication))
            .ToList();

        targets.Count.ShouldBeGreaterThan(5);
        foreach (var filters in targets)
        {
            var basic = filters.Single(IsBasicAuthentication);
            var instance = ((IFilterFactory)basic).CreateInstance(_services);
            instance.ShouldBeAssignableTo<IAsyncAuthorizationFilter>();
            var basicOrder = new FilterDescriptor(basic, FilterScope.Controller).Order;
            foreach (var other in filters.Where(f => !IsBasicAuthentication(f)))
                new FilterDescriptor(other, FilterScope.Controller).Order.ShouldBeGreaterThan(basicOrder);
        }
    }

    private static bool IsBasicAuthentication(IFilterMetadata f) =>
        f is ServiceFilterAttribute s && s.ServiceType == typeof(BasicAuthFilter);
}
