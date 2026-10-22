using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using Registry.Web.Controllers;
using Registry.Web.Models.DTO;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// UPL-01 A.2: the upload self-test endpoint discards the body and reports its size; its rate
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

        var result = (await controller.Probe(CancellationToken.None)).ShouldBeOfType<OkObjectResult>();

        var dto = result.Value.ShouldBeOfType<UploadProbeResultDto>();
        dto.Bytes.ShouldBe(size);
        dto.ServerMs.ShouldBeGreaterThanOrEqualTo(0);
        controller.Response.Headers.CacheControl.ToString().ShouldBe("no-store");
    }

    [Test]
    public async Task UploadProbePartition_LimitsEachUserSeparately()
    {
        static HttpContext As(string userId) => new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"))
        };

        var alice = RateLimitPolicies.UploadProbePartition(As("alice"));
        var bob = RateLimitPolicies.UploadProbePartition(As("bob"));
        alice.PartitionKey.ShouldNotBe(bob.PartitionKey);

        using var limiter = alice.Factory(alice.PartitionKey);
        for (var i = 0; i < RateLimitPolicies.UploadProbePermitsPerMinute; i++)
            (await limiter.AcquireAsync()).IsAcquired.ShouldBeTrue();

        (await limiter.AcquireAsync()).IsAcquired.ShouldBeFalse();
    }
}
