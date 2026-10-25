#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// A cancellation is answered by what cancelled it. A client that went away (the request's
/// <c>RequestAborted</c> is canceled) gets 499, logged at Debug, and nothing is written for it: no ProblemDetails,
/// no OGC exception report. A cancellation the client did not ask for (the host stopping, a job's token, an
/// internal timeout) is a 503 with <c>Retry-After</c> and a body (the HTTP case is in
/// <see cref="RenderAbortHttpTests"/>). Before, every cancellation was a 408, and the OGC filters wrote an
/// XML report with an empty message for it; the WMS and WMTS safety nets logged it at Error and rendered a
/// fallback image for nobody.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~CancellationResponseTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public sealed class CancellationResponseTests
{
    private const string Org = "org";
    private const string Ds = "ds";

    private static ProblemDetailsFactory Problems() =>
        new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider()
            .GetRequiredService<ProblemDetailsFactory>();

    private static DefaultHttpContext GoneClient(string path = "/orgs/org/ds/ds/thumb")
    {
        var aborted = new CancellationTokenSource();
        aborted.Cancel();
        var http = new DefaultHttpContext { RequestAborted = aborted.Token };
        http.Request.Path = path;
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static ExceptionContext ExceptionIn(HttpContext http, Exception ex) =>
        new(new ActionContext(http, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>())
        {
            Exception = ex
        };

    [Test]
    public async Task ApiFilter_ClientGone_499WithoutBody_LoggedAtDebug()
    {
        var log = new CapturingLogger<ApiErrorResponder>();
        var context = ExceptionIn(GoneClient(), new DdbCanceledException("Canceled by the cancel token"));

        await new ApiExceptionFilter(new ApiErrorResponder(Problems(), log)).OnExceptionAsync(context);

        context.ExceptionHandled.ShouldBeTrue();
        context.Result.ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(StatusCodes.Status499ClientClosedRequest);
        log.Count(LogLevel.Debug).ShouldBe(1);
        log.Entries.ShouldAllBe(e => e.Level == LogLevel.Debug);
    }

    [Test]
    public async Task ExceptionHandler_ClientGone_499_WritesNothing()
    {
        var http = GoneClient();
        var problems = new Mock<IProblemDetailsService>(MockBehavior.Strict);
        var handler = new ApiExceptionHandler(new ApiErrorResponder(Problems(), new CapturingLogger<ApiErrorResponder>()),
            problems.Object);

        (await handler.TryHandleAsync(http, new OperationCanceledException(), CancellationToken.None)).ShouldBeTrue();

        http.Response.StatusCode.ShouldBe(StatusCodes.Status499ClientClosedRequest);
        http.Response.Body.Length.ShouldBe(0);
    }

    [Test]
    public void OgcFilter_ClientGone_499WithoutAnExceptionReport()
    {
        var log = new CapturingLogger<OgcExceptionFilter>();
        var context = ExceptionIn(GoneClient("/orgs/org/ds/ds/wms"), new OperationCanceledException());

        new OgcExceptionFilter(log).OnException(context);

        context.ExceptionHandled.ShouldBeTrue();
        context.Result.ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(StatusCodes.Status499ClientClosedRequest);
        log.Entries.ShouldAllBe(e => e.Level == LogLevel.Debug);
    }

    [Test]
    public void OgcFilter_CancellationTheClientDidNotAskFor_Is503ServiceUnavailable()
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/orgs/org/ds/ds/wmts";
        var context = ExceptionIn(http, new OperationCanceledException());

        new OgcExceptionFilter(new CapturingLogger<OgcExceptionFilter>()).OnException(context);

        var result = context.Result.ShouldBeOfType<ContentResult>();
        result.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        result.Content!.ShouldContain("ServiceUnavailable");
        http.Response.Headers.RetryAfter.ToString().ShouldBe("5");
    }

    [Test]
    public async Task OgcAuthorization_ClientGone_499WithoutAnExceptionReport()
    {
        var http = GoneClient("/orgs/org/ds/ds/wms");
        var access = new Mock<IDatasetAccessResolver>();
        access.Setup(a => a.ResolveAsync(Org, Ds, AccessType.Read, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var routes = new RouteData { Values = { ["orgSlug"] = Org, ["dsSlug"] = Ds } };
        var context = new AuthorizationFilterContext(new ActionContext(http, routes, new ActionDescriptor()),
            new List<IFilterMetadata>());
        var log = new CapturingLogger<OgcAuthorizationFilter>();

        await new OgcAuthorizationFilter(access.Object, log).OnAuthorizationAsync(context);

        context.Result.ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(StatusCodes.Status499ClientClosedRequest);
        log.Entries.ShouldAllBe(e => e.Level == LogLevel.Debug);
    }

    [Test]
    public async Task WmsGetMap_ACanceledRender_IsNotTurnedIntoAFallbackImage()
    {
        var log = new CapturingLogger<WmsManager>();
        var cache = new Mock<ICacheManager>();
        cache.Setup(c => c.GetAsync(MagicStrings.OgcRenderCacheSeed, It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<object[]>()))
            .ThrowsAsync(new DdbCanceledException("Canceled by the cancel token"));
        var wms = new WmsManager(OgcDependencies(), log, cache.Object, Mock.Of<IRasterIndexCatalog>());

        await Should.ThrowAsync<OperationCanceledException>(() => wms.GetMapAsync(Org, Ds, ["ortho"], [""],
            [0, 0, 10, 10], "EPSG:3857", 256, 256, "image/png", null, true));

        log.Count(LogLevel.Error).ShouldBe(0);
    }

    [Test]
    public async Task WmtsGetTile_ACanceledRender_IsNotTurnedIntoAFallbackTile()
    {
        var log = new CapturingLogger<WmtsManager>();
        var tiles = new Mock<IOgcRasterTileSource>();
        tiles.Setup(t => t.GetTileAsync(Org, Ds, It.IsAny<IDDB>(), It.IsAny<OgcLayerDto>(), 3, 1, 2, "png",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DdbCanceledException("Canceled by the cancel token"));
        var wmts = new WmtsManager(OgcDependencies(), log, tiles.Object);

        await Should.ThrowAsync<OperationCanceledException>(() => wmts.GetTileAsync(Org, Ds, "ortho",
            WmtsConformance.DefaultStyle, WmtsConformance.DefaultTileMatrixSet, 3, 1, 2, "image/png"));

        log.Count(LogLevel.Error).ShouldBe(0);
    }

    private static OgcManagerDependencies OgcDependencies() =>
        new(Access(), Mock.Of<IBuildArtifactResolver>(), Mock.Of<IDdbWrapper>(), Catalog(), TestCaches.Records(),
            new HttpContextAccessor());

    private static IDatasetAccessResolver Access()
    {
        var dataset = new Dataset { Slug = Ds, InternalRef = Guid.NewGuid(), Organization = new Organization { Slug = Org } };
        var access = new Mock<IDatasetAccessResolver>();
        access.Setup(a => a.ResolveAsync(Org, Ds, AccessType.Read, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatasetAccess(dataset, () => Mock.Of<IDDB>()));
        return access.Object;
    }

    private static IOgcLayerCatalog Catalog()
    {
        var catalog = new Mock<IOgcLayerCatalog>();
        catalog.Setup(c => c.ResolveAsync(Org, Ds, "ortho", null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OgcLayerDto
            {
                Name = "ortho", EntryType = EntryType.GeoRaster, EntryPath = "ortho.tif", EntryHash = "h1"
            });
        return catalog.Object;
    }
}
