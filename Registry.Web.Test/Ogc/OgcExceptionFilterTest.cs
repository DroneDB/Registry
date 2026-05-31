using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Utilities.Ogc;
using Shouldly;

namespace Registry.Web.Test.Ogc;

/// <summary>
/// Smoke tests for the rewritten OgcExceptionFilter: service-kind discrimination
/// (/wmts vs /wms prefix collision), WMS version negotiation, and the new
/// DdbException mapping (BUILDDEPMISSING / BUILDINPROGRESS → 503).
/// </summary>
[TestFixture]
public class OgcExceptionFilterTest
{
    private static ExceptionContext MakeContext(string path, Exception ex, params (string k, string v)[] q)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        if (q.Length > 0)
        {
            var dict = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>();
            foreach (var (k, v) in q) dict[k] = v;
            http.Request.Query = new QueryCollection(dict);
        }
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return new ExceptionContext(action, new List<IFilterMetadata>()) { Exception = ex };
    }

    private static OgcExceptionFilter NewFilter() =>
        new(NullLogger<OgcExceptionFilter>.Instance);

    [Test]
    public void DdbException_BuildDepMissing_Maps503ServiceUnavailable()
    {
        var ctx = MakeContext("/orgs/o/ds/d/wms",
            new DdbException("DDB error: BUILDDEPMISSING (cog)"));
        NewFilter().OnException(ctx);
        var r = (ContentResult)ctx.Result!;
        r.StatusCode.ShouldBe(503);
        r.Content.ShouldContain("ServiceUnavailable");
    }

    [Test]
    public void DdbException_BuildInProgress_Maps503ServiceUnavailable()
    {
        var ctx = MakeContext("/orgs/o/ds/d/wfs",
            new DdbException("BUILDINPROGRESS: cog under construction"));
        NewFilter().OnException(ctx);
        var r = (ContentResult)ctx.Result!;
        r.StatusCode.ShouldBe(503);
        r.Content.ShouldContain("ServiceUnavailable");
    }

    [Test]
    public void DdbException_Generic_Maps500NoApplicableCode()
    {
        var ctx = MakeContext("/orgs/o/ds/d/wms", new DdbException("native crash"));
        NewFilter().OnException(ctx);
        var r = (ContentResult)ctx.Result!;
        r.StatusCode.ShouldBe(500);
        r.Content.ShouldContain("NoApplicableCode");
    }

    [Test]
    public void ConflictException_Maps409()
    {
        var ctx = MakeContext("/orgs/o/ds/d/wms", new ConflictException("conflict"));
        NewFilter().OnException(ctx);
        var r = (ContentResult)ctx.Result!;
        r.StatusCode.ShouldBe(409);
    }

    [Test]
    public void WmtsPath_NotMisclassifiedAsWms()
    {
        // A WMTS request must not produce WMS 1.3.0 ServiceExceptionReport XML.
        var ctx = MakeContext("/orgs/o/ds/d/wmts",
            new OgcException("OperationNotSupported", "Unknown op", 400));
        NewFilter().OnException(ctx);
        var r = (ContentResult)ctx.Result!;
        // ows:ExceptionReport is used for WMTS, not ServiceExceptionReport.
        r.Content.ShouldNotContain("ServiceExceptionReport");
        r.Content.ShouldContain("ExceptionReport");
    }

    [Test]
    public void WmsPath_VersionNegotiation_DefaultsTo130()
    {
        var ctx = MakeContext("/orgs/o/ds/d/wms",
            new OgcException("InvalidCRS", "bad CRS", 400, "CRS"));
        NewFilter().OnException(ctx);
        var r = (ContentResult)ctx.Result!;
        r.StatusCode.ShouldBe(400);
        r.Content.ShouldContain("InvalidCRS");
    }

    [Test]
    public void WmsPath_Version111_ProducesServiceExceptionReport()
    {
        var ctx = MakeContext("/orgs/o/ds/d/wms",
            new OgcException("InvalidFormat", "bad format", 400, "FORMAT"),
            ("VERSION", "1.1.1"));
        NewFilter().OnException(ctx);
        var r = (ContentResult)ctx.Result!;
        r.Content.ShouldContain("ServiceExceptionReport");
    }
}
