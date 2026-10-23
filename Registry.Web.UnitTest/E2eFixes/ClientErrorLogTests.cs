#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;
using Serilog.Core;
using Shouldly;

namespace Registry.Web.Test.E2eFixes;

/// <summary>
/// End-to-end check of Registry eb1311f2 + libddb 2.0.0, finding 5: an expected denial (<c>401</c>, <c>403</c>, any
/// <c>4xx</c>) logged <c>Exception in /orgs/.../ds/...</c> with its full stack at Information. The classifier's
/// outcome is logged once at its level: a client error as one line (method, route, status, message) without the
/// stack, a server fault (<c>5xx</c>) with the exception.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~ClientErrorLogTests"
/// </summary>
[TestFixture]
public sealed class ClientErrorLogTests
{
    private static ProblemDetailsFactory Problems() =>
        new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider()
            .GetRequiredService<ProblemDetailsFactory>();

    private static DefaultHttpContext Request(bool authenticated = false)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Path = "/orgs/org/ds/private";
        http.Response.Body = new System.IO.MemoryStream();
        if (authenticated)
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "user")], "test"));
        return http;
    }

    public static IEnumerable<TestCaseData> ClientErrors()
    {
        yield return new TestCaseData(new ForbiddenException("The current user is not allowed to read this dataset"), false, 401).SetName("{m}(anonymous denied)");
        yield return new TestCaseData(new ForbiddenException("The current user is not allowed to read this dataset"), true, 403).SetName("{m}(signed-in denied)");
        yield return new TestCaseData(new UnauthorizedException("Invalid credentials"), false, 401).SetName("{m}(unauthorized)");
        yield return new TestCaseData(new NotFoundException("Cannot find dataset"), true, 404).SetName("{m}(not found)");
        yield return new TestCaseData(new BadRequestException("Invalid slug"), true, 400).SetName("{m}(bad request)");
        yield return new TestCaseData(new ConflictException("Already exists"), true, 409).SetName("{m}(conflict)");
        yield return new TestCaseData(new DdbInvalidArgumentsException("bad path"), true, 400).SetName("{m}(libddb invalid arguments)");
    }

    [TestCaseSource(nameof(ClientErrors))]
    public void ApiErrorResponder_ClientError_IsOneLineWithoutAStack(Exception exception, bool authenticated, int status)
    {
        var log = new CapturingLogger<ApiErrorResponder>();
        var http = Request(authenticated);

        new ApiErrorResponder(Problems(), log).Describe(http, exception);

        var record = log.Records.ShouldHaveSingleItem();
        record.Exception.ShouldBeNull("an expected client error carries no stack");
        record.Message.ShouldContain(status.ToString());
        record.Message.ShouldContain("/orgs/org/ds/private");
        record.Message.ShouldContain(exception.Message);
        record.Level.ShouldBe(ApiExceptionClassifier.Classify(exception, authenticated).Level);
    }

    public static IEnumerable<TestCaseData> ServerFaults()
    {
        yield return new TestCaseData(new InvalidOperationException("boom"), 500, LogLevel.Error).SetName("{m}(unexpected)");
        yield return new TestCaseData(new DdbException("engine failure"), 500, LogLevel.Error).SetName("{m}(libddb failure)");
        yield return new TestCaseData(new DdbBusyException("busy"), 503, LogLevel.Warning).SetName("{m}(busy)");
        yield return new TestCaseData(new DdbRetryableException("disk full", "noSpaceOnDevice"), 503, LogLevel.Warning).SetName("{m}(retryable)");
    }

    [TestCaseSource(nameof(ServerFaults))]
    public void ApiErrorResponder_ServerFault_KeepsTheStack(Exception exception, int status, LogLevel level)
    {
        var log = new CapturingLogger<ApiErrorResponder>();
        var http = Request();

        var (descriptor, _) = new ApiErrorResponder(Problems(), log).Describe(http, exception);

        descriptor.StatusCode.ShouldBe(status);
        var record = log.Records.ShouldHaveSingleItem();
        record.Exception.ShouldBeSameAs(exception);
        record.Level.ShouldBe(level);
        record.Message.ShouldContain("/orgs/org/ds/private");
    }

    [Test]
    public void ApiErrorResponder_ClientGone_IsOneDebugLineWithoutAStack()
    {
        var log = new CapturingLogger<ApiErrorResponder>();
        var http = Request();
        var aborted = new System.Threading.CancellationTokenSource();
        aborted.Cancel();
        http.RequestAborted = aborted.Token;

        new ApiErrorResponder(Problems(), log).Describe(http, new OperationCanceledException());

        var record = log.Records.ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Debug);
        record.Exception.ShouldBeNull();
    }

    [Test]
    public void OgcFilter_ClientError_IsOneLineWithoutAStack_AndServerFaultKeepsIt()
    {
        var log = new CapturingLogger<OgcExceptionFilter>();
        var filter = new OgcExceptionFilter(log);

        filter.OnException(OgcContext(new ForbiddenException("not allowed")));
        var fault = new InvalidOperationException("boom");
        filter.OnException(OgcContext(fault));

        log.Records.Count.ShouldBe(2);
        log.Records[0].Exception.ShouldBeNull();
        log.Records[0].Message.ShouldContain("401");
        log.Records[1].Exception.ShouldBeSameAs(fault);
    }

    private static ExceptionContext OgcContext(Exception exception)
    {
        var http = Request();
        http.Request.Path = "/orgs/org/ds/private/wms";
        return new ExceptionContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>())
        {
            Exception = exception
        };
    }
}

/// <summary>The same through HTTP on the host: what a denied request writes to the log.</summary>
[TestFixture]
[NonParallelizable]
public sealed class ClientErrorLogHttpTests : IDisposable
{
    private const string OwnerUser = "client-error-owner";
    private const string OtherUser = "client-error-other";
    private const string Org = "client-error-org";
    private const string Ds = "client-error-ds";

    private readonly LogCapture _logs = new();
    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory(
            new Dictionary<string, string> { ["Serilog:LevelSwitches:$controlSwitch"] = "Information" },
            services => services.AddSingleton<ILogEventSink>(_logs));
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(OwnerUser);
        await _factory.CreateUserAsync(OtherUser);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: false);
        Seed.Dataset(context, org, Ds);
        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [SetUp]
    public void SetUp() => _logs.Clear();

    [TestCase(null, HttpStatusCode.Unauthorized)]
    [TestCase(OtherUser, HttpStatusCode.Forbidden)]
    public async Task DeniedRead_LogsOneLine_NoStack(string? user, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/orgs/{Org}/ds/{Ds}");
        using var response = await _client.SendAsync(user == null ? request : request.AsUser(user));

        response.StatusCode.ShouldBe(expected);
        var incident = _logs.Records.Where(r => r.Message.Contains($"/orgs/{Org}/ds/{Ds}") &&
                                                  r.Category.Contains(nameof(ApiErrorResponder))).ToArray();
        incident.ShouldHaveSingleItem().Exception.ShouldBeNull("a denial is expected: no stack");
        _logs.Records.ShouldAllBe(r => r.Exception == null, "nothing in this request logged a stack");
    }

    [Test]
    public async Task MissingDataset_Is404_NoStack()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/orgs/{Org}/ds/nowhere").AsUser(OwnerUser);
        using var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        _logs.Records.ShouldAllBe(r => r.Exception == null);
    }
}
