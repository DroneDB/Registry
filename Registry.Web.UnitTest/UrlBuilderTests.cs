using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>The public URLs (<see cref="Services.Adapters.UrlBuilder"/>).</summary>
[TestFixture]
public class UrlBuilderTests
{
    private static readonly Dataset Dataset = new() { Slug = "survey", Organization = new Organization { Slug = "acme" } };

    [TestCase("https://hub.example", false, "https://hub.example")]
    [TestCase("https://hub.example:8443/", false, "https://hub.example:8443")]
    [TestCase("http://hub.example", true, "ddb+unsafe://hub.example")]
    [TestCase("https://hub.example", true, "ddb://hub.example")]
    public void ExternalUrlOverride_IsTheHost(string external, bool ddbScheme, string expected)
    {
        var urls = TestPorts.Urls(Microsoft.Extensions.Options.Options.Create(
            new HttpHostOptions { ExternalUrlOverride = external }));

        urls.GetLocalHost(ddbScheme).ShouldBe(expected);
    }

    [Test]
    public void WithoutOverride_TheRequestsHost()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("registry.local:5000");
        context.Request.Scheme = "https";
        var urls = TestPorts.Urls(accessor: new HttpContextAccessor { HttpContext = context });

        urls.GenerateDatasetUrl(Dataset).ShouldBe("https://registry.local:5000/orgs/acme/ds/survey");
        urls.GenerateDatasetUrl(Dataset, useDdbScheme: true).ShouldBe("ddb://registry.local:5000/acme/survey");
    }

    [Test]
    public void WithoutARequest_Localhost()
    {
        TestPorts.Urls().GetLocalHost().ShouldBe("http://localhost");
    }
}
