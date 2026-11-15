using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using Registry.Web.Controllers;
using Registry.Web.Models.DTO;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The upload self-test endpoint discards the body and reports its size; its rate
/// limit is one fixed window per user.
/// </summary>
[TestFixture]
public class UploadProbeTests
{
    [Test]
    public async Task Probe_ReadsTheWholeBody_AndReportsItsSize()
    {
        const int size = 3 * 1024 * 1024 + 17;
        var controller = new UploadProbeController
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { Request = { Body = new MemoryStream(new byte[size]) } }
            }
        };

        var result = await controller.Probe(CancellationToken.None);

        var dto = result.Value.ShouldBeOfType<UploadProbeResultDto>();
        dto.Bytes.ShouldBe(size);
        dto.ServerMs.ShouldBeGreaterThanOrEqualTo(0);
        controller.Response.Headers.CacheControl.ToString().ShouldBe("no-store");
    }

    [Test]
    public async Task UploadProbePartition_LimitsEachUserSeparately()
    {
        // The principal is the one of the token issuer: the user id is the name claim
        static HttpContext As(string userId) => new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, userId)], "test"))
        };

        var alice = RateLimitPolicies.UploadProbePartition(As("alice"));
        var bob = RateLimitPolicies.UploadProbePartition(As("bob"));
        alice.PartitionKey.ShouldNotBe(bob.PartitionKey);

        using var limiter = alice.Factory(alice.PartitionKey);
        for (var i = 0; i < RateLimitPolicies.UploadProbePermitsPerMinute; i++)
            (await limiter.AcquireAsync()).IsAcquired.ShouldBeTrue();

        (await limiter.AcquireAsync()).IsAcquired.ShouldBeFalse();
    }
    [Test]
    public async Task ASpentBudget_IsRefusedWithProblemDetailsAndRetryAfter()
    {
        using var factory = new RegistryWebFactory();
        await factory.CreateUserAsync("prober");
        using var client = factory.CreateClient();

        HttpRequestMessage Probe()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/sys/upload-probe")
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return request.AsUser("prober");
        }

        for (var i = 0; i < RateLimitPolicies.UploadProbePermitsPerMinute; i++)
        {
            using var allowed = await client.SendAsync(Probe());
            allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var refused = await client.SendAsync(Probe());
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        refused.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        refused.Headers.RetryAfter.ShouldNotBeNull();
        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetInt32().ShouldBe(429);
        body.RootElement.GetProperty("detail").GetString().ShouldBe(RateLimitPolicies.TooManyRequestsMessage);
    }
}
