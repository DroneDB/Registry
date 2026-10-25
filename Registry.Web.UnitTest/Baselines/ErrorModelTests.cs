#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;
using Registry.Ports.DroneDB;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The error model, through HTTP on <see cref="RegistryWebFactory"/>:
/// <list type="bullet">
/// <item>Every error body is an RFC 9457 <c>ProblemDetails</c> (<c>application/problem+json</c>, with
/// <c>status</c>, <c>title</c>, <c>detail</c>, <c>traceId</c> and the <c>noRetry</c> extension), from MVC
/// actions, from model validation and from middleware (the <c>IExceptionHandler</c> path).</item>
/// <item>A missing permission answers 403 to an authenticated caller and 401 to an anonymous one.</item>
/// <item>One 413 rule: <c>[EnforceContentLength]</c> rejects a declared <c>Content-Length</c> above the limit.</item>
/// <item>Four endpoint findings: <c>import/sources</c> checks access, member management disabled
/// is a 403 after the access check, a STAC path that is not Base64 is a 400, an unknown Hangfire page is
/// not a 500.</item>
/// </list>
/// /// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~ErrorModelTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class ErrorModelTests : IDisposable
{
    private const string ProblemJson = "application/problem+json";
    private const string OwnerUser = "errormodel-owner";
    private const string RegularUser = "errormodel-user";
    private const string PrivateOrg = "errormodel-private-org";
    private const string PrivateDs = "errormodel-private-ds";
    private const long MaxRequestBodySize = 1024;
    private const long MaxChunkBytes = 1024;

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory(
            new Dictionary<string, string>
            {
                ["AppSettings:MaxRequestBodySize"] = MaxRequestBodySize.ToString(CultureInfo.InvariantCulture),
                ["AppSettings:Uploads:MaxChunkBytes"] = MaxChunkBytes.ToString(CultureInfo.InvariantCulture)
            },
            services =>
            {
                services.AddTransient<ITokenManager, FaultInjectingTokenManager>();
                services.AddSingleton<IDownloadLimiter>(new NoFreeDownloadSlot());
            });

        // A dataset without visibility metadata is private (native ddb throws on a missing key)
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new Registry.Ports.DroneDB.DdbException("Key not found"));

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(OwnerUser);
        await _factory.CreateUserAsync(RegularUser);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, PrivateOrg, ownerId: owner.Id, isPublic: false);
        Seed.Dataset(context, org, PrivateDs);
        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    // ---- 401 only for anonymous callers ----

    [Test]
    public async Task PrivateDataset_AuthenticatedNonMember_Is403ProblemDetails()
    {
        var (response, problem) = await SendAsync(HttpMethod.Get, $"/orgs/{PrivateOrg}/ds/{PrivateDs}", RegularUser);

        ShouldBeProblem(response, problem, StatusCodes.Status403Forbidden);
        problem["detail"]!.GetValue<string>().ShouldBe("The current user is not allowed to read this dataset"); // the access denial message
        problem["noRetry"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Test]
    public async Task PrivateDataset_Anonymous_Is401ProblemDetails()
    {
        var (response, problem) = await SendAsync(HttpMethod.Get, $"/orgs/{PrivateOrg}/ds/{PrivateDs}");

        ShouldBeProblem(response, problem, StatusCodes.Status401Unauthorized);
        problem["detail"]!.GetValue<string>().ShouldBe("The current user is not allowed to read this dataset"); // the access denial message
        problem["noRetry"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Test]
    public async Task AdminOnlyManagerAction_NonAdminUser_Is403()
    {
        // GET /users passes the class-level [Authorize]; the Admin policy refuses (one message)
        var (response, problem) = await SendAsync(HttpMethod.Get, "/users", RegularUser);

        ShouldBeProblem(response, problem, StatusCodes.Status403Forbidden);
        problem["detail"]!.GetValue<string>().ShouldBe("Only administrators can perform this operation");
    }

    [Test]
    public async Task BasicAuthEndpoint_ChallengesAnonymousCallersOnly()
    {
        // [BasicAuthentication] endpoints (QGIS, scripts): a 401 carries the Basic challenge so the client
        // asks for credentials; a signed-in caller without access gets a 403 and no challenge. (MVT, the
        // other [BasicAuthentication] controller, answers in the OGC envelope: DatasetAccessBehaviourTests)
        var url = $"/orgs/{PrivateOrg}/ds/{PrivateDs}/build/abc/file.bin";

        var (anonymous, anonymousProblem) = await SendAsync(HttpMethod.Get, url);
        ShouldBeProblem(anonymous, anonymousProblem, StatusCodes.Status401Unauthorized);
        anonymous.Headers.WwwAuthenticate.Select(h => h.Scheme).ShouldContain("Basic");

        var (user, userProblem) = await SendAsync(HttpMethod.Get, url, RegularUser);
        ShouldBeProblem(user, userProblem, StatusCodes.Status403Forbidden);
        user.Headers.WwwAuthenticate.ShouldBeEmpty();
    }

    [Test]
    public async Task FailedLogin_Anonymous_StaysA401ProblemDetails()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/users/authenticate")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
                { ["username"] = RegularUser, ["password"] = "wrong-password" })
        };
        var (response, problem) = await SendAsync(request);

        ShouldBeProblem(response, problem, StatusCodes.Status401Unauthorized);
    }

    // ---- ProblemDetails from MVC, model validation and middleware ----

    [Test]
    public async Task MissingDataset_Is404ProblemDetails()
    {
        var (response, problem) = await SendAsync(HttpMethod.Get, $"/orgs/{PrivateOrg}/ds/errormodel-missing", OwnerUser);

        ShouldBeProblem(response, problem, StatusCodes.Status404NotFound);
        problem["detail"]!.GetValue<string>().ShouldBe($"Cannot find dataset errormodel-missing in organization {PrivateOrg}");
        problem["noRetry"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Test]
    public async Task InvalidModel_IsValidationProblemDetailsWithTheFieldMessageAsDetail()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/orgs/{PrivateOrg}/ds/{PrivateDs}/meta/remove")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>())
        };
        var (response, problem) = await SendAsync(request.AsUser(OwnerUser));

        ((int)response.StatusCode).ShouldBe(StatusCodes.Status400BadRequest);
        problem.ShouldNotBeNull();
        problem["status"]!.GetValue<int>().ShouldBe(StatusCodes.Status400BadRequest);
        // The Hub shows `detail`: it keeps the field message the old `error` member carried
        problem["detail"]!.GetValue<string>().ShouldBe("The id field is required.");
        problem["errors"]!["id"]!.AsArray().Select(e => e!.GetValue<string>())
            .ShouldBe(["The id field is required."]);
        problem["noRetry"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Test]
    public async Task BareNotFoundResult_IsProblemDetailsWithNoRetry()
    {
        // MvtController answers NotFound() without a body for a tile that was never built: the
        // framework's ProblemDetails gets the noRetry member too (AddProblemDetails customization).
        // No vector of the dataset has the hash: the tiles are looked up by role among the vectors
        _factory.DdbWrapper.Setup(d => d.ListEntrySummaries(It.IsAny<string>(), It.IsAny<EntryQuery>())).Returns([]);
        var (response, problem) = await SendAsync(HttpMethod.Get,
            $"/orgs/{PrivateOrg}/ds/{PrivateDs}/mvt/abc/0/0/0.pbf", OwnerUser);

        ((int)response.StatusCode).ShouldBe(StatusCodes.Status404NotFound);
        problem.ShouldNotBeNull();
        problem["noRetry"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Test]
    public async Task UnexpectedServerError_InAnAction_Is500WithAGenericDetail()
    {
        // An exception the classifier does not know: a server fault
        _factory.DdbWrapper.Setup(d => d.GetStamp(It.IsAny<string>()))
            .Throws(new InvalidOperationException("secret engine detail"));
        try
        {
            var (response, problem) = await SendAsync(HttpMethod.Get,
                $"/orgs/{PrivateOrg}/ds/{PrivateDs}/stamp", OwnerUser);

            ShouldBeProblem(response, problem, StatusCodes.Status500InternalServerError);
            problem["detail"]!.GetValue<string>().ShouldBe("Internal server error");
            problem.ToJsonString().ShouldNotContain("secret engine detail");
        }
        finally
        {
            _factory.DdbWrapper.Setup(d => d.GetStamp(It.IsAny<string>())).CallBase();
        }
    }

    [Test]
    public async Task MiddlewareException_IsHandledAsProblemDetails()
    {
        var (response, problem) = await SendAsync(Fault(HttpMethod.Get, "/sys/version", FaultInjectingTokenManager.Crash));

        ShouldBeProblem(response, problem, StatusCodes.Status500InternalServerError);
        problem["detail"]!.GetValue<string>().ShouldBe("Internal server error");
        problem.ToJsonString().ShouldNotContain(FaultInjectingTokenManager.CrashMessage);
    }

    [Test]
    public async Task MiddlewareRegistryException_UsesTheClassifier()
    {
        var (notFound, notFoundProblem) =
            await SendAsync(Fault(HttpMethod.Get, "/sys/version", FaultInjectingTokenManager.Missing));
        ShouldBeProblem(notFound, notFoundProblem, StatusCodes.Status404NotFound);
        notFoundProblem["detail"]!.GetValue<string>().ShouldBe(FaultInjectingTokenManager.MissingMessage);

        var (busy, busyProblem) =
            await SendAsync(Fault(HttpMethod.Get, "/sys/version", FaultInjectingTokenManager.Busy));
        ShouldBeProblem(busy, busyProblem, StatusCodes.Status503ServiceUnavailable);
        busy.Headers.RetryAfter?.Delta.ShouldBe(TimeSpan.FromSeconds(FaultInjectingTokenManager.BusyRetryAfter));
        busyProblem["noRetry"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Test]
    public async Task MiddlewareException_OnAMinimalEndpointRoute_IsHandledAsProblemDetails()
    {
        // /version is a minimal endpoint (endpoints.MapGet): no MVC filter runs on this route
        var (response, problem) = await SendAsync(Fault(HttpMethod.Get, "/version", FaultInjectingTokenManager.Crash));

        ShouldBeProblem(response, problem, StatusCodes.Status500InternalServerError);
    }

    // ---- 413: [EnforceContentLength] ----

    [Test]
    public async Task Upload_DeclaredLengthAboveTheLimit_Is413ProblemDetailsBeforeTheBodyIsRead()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/orgs/{PrivateOrg}/ds/{PrivateDs}/obj")
        {
            Content = Multipart(MaxRequestBodySize * 2)
        };
        var (response, problem) = await SendAsync(request.AsUser(OwnerUser));

        ShouldBeProblem(response, problem, StatusCodes.Status413PayloadTooLarge);
        problem["detail"]!.GetValue<string>()
            .ShouldBe($"Upload exceeds the maximum allowed size of {MaxRequestBodySize} bytes");
        problem["noRetry"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Test]
    public async Task Chunk_DeclaredLengthAboveTheChunkLimit_Is413ProblemDetails()
    {
        // The chunk limit allows 64 KiB of multipart framing on top of MaxChunkBytes
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/orgs/{PrivateOrg}/ds/{PrivateDs}/obj/chunk")
        {
            Content = Multipart(MaxChunkBytes + 128 * 1024)
        };
        var (response, problem) = await SendAsync(request.AsUser(OwnerUser));

        ShouldBeProblem(response, problem, StatusCodes.Status413PayloadTooLarge);
        problem["detail"]!.GetValue<string>().ShouldBe($"A chunk can be at most {MaxChunkBytes} bytes");
    }

    // ---- 429 from a middleware ----

    [Test]
    public async Task DownloadLimit_JsonCaller_Is429ProblemDetailsWithRetryAfter()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/orgs/{PrivateOrg}/ds/{PrivateDs}/download?path=a")
            .AsUser(RegularUser);
        request.Headers.Add("X-Download-Check", "1");
        var (response, problem) = await SendAsync(request);

        ShouldBeProblem(response, problem, StatusCodes.Status429TooManyRequests);
        response.Headers.RetryAfter?.Delta.ShouldBe(TimeSpan.FromSeconds(10));
        problem["detail"]!.GetValue<string>()
            .ShouldBe("Too many concurrent downloads. Please wait for a download to finish.");
        problem["noRetry"]!.GetValue<bool>().ShouldBeFalse();
    }

    // ---- four endpoint findings ----

    [Test]
    public async Task ImportSources_RequiresWriteAccessToTheOrganization()
    {
        var url = $"/orgs/{PrivateOrg}/import/sources";

        var (anonymous, anonymousProblem) = await SendAsync(HttpMethod.Get, url);
        ShouldBeProblem(anonymous, anonymousProblem, StatusCodes.Status401Unauthorized);

        var (user, userProblem) = await SendAsync(HttpMethod.Get, url, RegularUser);
        ShouldBeProblem(user, userProblem, StatusCodes.Status403Forbidden);

        var (owner, _) = await SendAsync(HttpMethod.Get, url, OwnerUser);
        ((int)owner.StatusCode).ShouldBe(StatusCodes.Status200OK);
    }

    [Test]
    public async Task RemoveMember_WithMemberManagementDisabled_ChecksAccessFirstThenAnswers403()
    {
        var url = $"/orgs/{PrivateOrg}/members/{RegularUser}";

        var (anonymous, anonymousProblem) = await SendAsync(HttpMethod.Delete, url);
        ShouldBeProblem(anonymous, anonymousProblem, StatusCodes.Status401Unauthorized);

        var (user, userProblem) = await SendAsync(HttpMethod.Delete, url, RegularUser);
        ShouldBeProblem(user, userProblem, StatusCodes.Status403Forbidden);

        var (owner, ownerProblem) = await SendAsync(HttpMethod.Delete, url, OwnerUser);
        ShouldBeProblem(owner, ownerProblem, StatusCodes.Status403Forbidden);
        ownerProblem["detail"]!.GetValue<string>().ShouldBe("Organization member management is disabled");
        ownerProblem["noRetry"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Test]
    public async Task StacChild_PathNotBase64_Is400()
    {
        var (response, problem) = await SendAsync(HttpMethod.Get,
            $"/orgs/{PrivateOrg}/ds/{PrivateDs}/stac/not*base64", OwnerUser);

        ShouldBeProblem(response, problem, StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task HangfireDashboard_UnknownPage_IsNotAServerError()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/hangfire/errormodel-no-such-page").AsUser(RegularUser);
        using var response = await _client.SendAsync(request);

        ((int)response.StatusCode).ShouldBeLessThan(500);
    }

    [TestCase(null, StatusCodes.Status401Unauthorized)]
    [TestCase(RegularUser, StatusCodes.Status403Forbidden)]
    [TestCase(RegistryWebFactory.AdminUserName, StatusCodes.Status200OK)]
    public async Task HangfireDashboard_RegisteredOnce_StillAdminOnly(string? user, int status)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/hangfire");
        if (user != null)
            request.AsUser(user, isAdmin: user == RegistryWebFactory.AdminUserName);
        using var response = await _client.SendAsync(request);

        ((int)response.StatusCode).ShouldBe(status);
    }

    // ---- helpers ----

    private static void ShouldBeProblem(HttpResponseMessage response, [NotNull] JsonObject? problem, int status)
    {
        ((int)response.StatusCode).ShouldBe(status);
        response.Content.Headers.ContentType?.MediaType.ShouldBe(ProblemJson);
        problem.ShouldNotBeNull();
        problem["status"]!.GetValue<int>().ShouldBe(status);
        problem["title"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        problem["traceId"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        problem.ContainsKey("noRetry").ShouldBeTrue("noRetry is an extension member of every error body");
        problem.ContainsKey("error").ShouldBeFalse("the ErrorResponse body is gone");
    }

    private Task<(HttpResponseMessage Response, JsonObject? Problem)> SendAsync(HttpMethod method, string url,
        string? user = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (user != null)
            request.AsUser(user);
        return SendAsync(request);
    }

    private async Task<(HttpResponseMessage Response, JsonObject? Problem)> SendAsync(HttpRequestMessage request)
    {
        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        JsonObject? problem = null;
        if (body.TrimStart().StartsWith('{'))
            problem = JsonNode.Parse(body) as JsonObject;
        return (response, problem);
    }

    private static HttpRequestMessage Fault(HttpMethod method, string url, string fault)
    {
        // Signed in: the token check runs after authentication and authorization
        var request = new HttpRequestMessage(method, url).AsUser(RegularUser);
        request.Headers.Add(FaultInjectingTokenManager.Header, fault);
        return request;
    }

    private static MultipartFormDataContent Multipart(long bytes)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("errormodel.bin"), "path");
        var file = new ByteArrayContent(new byte[bytes]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", "errormodel.bin");
        return content;
    }

    /// <summary>Download limiter that is on and has no free slot.</summary>
    private sealed class NoFreeDownloadSlot : IDownloadLimiter
    {
        public bool IsEnabled => true;
        public bool TryAcquireSlot(string key) => false;
        public void ReleaseSlot(string key) { }
        public bool CanAcquireSlot(string key) => false;
    }

    /// <summary>
    /// <see cref="ITokenManager"/> as seen by <c>TokenManagerMiddleware</c> (a middleware inside the app
    /// pipeline): throws the exception the <see cref="Header"/> asks for, otherwise lets the request through.
    /// </summary>
    private sealed class FaultInjectingTokenManager(IHttpContextAccessor accessor) : ITokenManager
    {
        public const string Header = "X-Fault-Injection";
        public const string Crash = "crash";
        public const string Missing = "missing";
        public const string Busy = "busy";
        public const string CrashMessage = "middleware secret";
        public const string MissingMessage = "middleware lost it";
        public const int BusyRetryAfter = 7;

        public bool IsCurrentActiveToken() =>
            accessor.HttpContext?.Request.Headers[Header].ToString() switch
            {
                Crash => throw new InvalidOperationException(CrashMessage),
                Missing => throw new NotFoundException(MissingMessage),
                Busy => throw new TransientException("middleware busy", BusyRetryAfter),
                _ => true
            };
    }
}
