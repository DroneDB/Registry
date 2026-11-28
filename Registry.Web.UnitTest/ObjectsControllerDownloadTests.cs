using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Controllers;
using Registry.Web.Services.Objects;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Regression tests for the Download path parsing, GET (query) and POST (form): with multiple "path"
/// occurrences every value is taken verbatim (file names may contain
/// commas and ampersands), while a single occurrence keeps the legacy
/// comma-split behaviour used by already-shared download links.
/// </summary>
[TestFixture]
public class ObjectsControllerDownloadTests
{
    private sealed class DownloadSentinel : Exception
    {
        public DownloadSentinel(string[] captured)
        {
            Captured = captured;
        }

        public string[] Captured { get; }
    }

    private static Mock<IEntryQueryService> CreateQueries()
    {
        var queries = new Mock<IEntryQueryService>(MockBehavior.Strict);

        // Everything is treated as a directory so even single-path requests
        // fall through to DownloadAsync, where the parsed array is captured.
        queries.Setup(m => m.GetEntryTypeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(EntryType.Directory);

        queries.Setup(m => m.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>(),
                It.IsAny<CancellationToken>()))
            .Callback((string _, string __, string[] paths, CancellationToken ___) => throw new DownloadSentinel(paths));

        return queries;
    }

    // The paths the controller hands to the archive, for a GET (query) or a POST (form) with these "path" values
    private static async Task<string[]> CaptureParsedPaths(bool post, params string[] pathValues)
    {
        var httpContext = new DefaultHttpContext();
        var values = new Dictionary<string, StringValues>();
        if (pathValues.Length > 0)
            values["path"] = new StringValues(pathValues);
        if (post)
        {
            httpContext.Request.Method = HttpMethods.Post;
            httpContext.Request.ContentType = "application/x-www-form-urlencoded";
            httpContext.Request.Form = new FormCollection(values);
        }
        else
        {
            httpContext.Request.Query = new QueryCollection(values);
        }

        var controller = new ObjectDownloadsController(CreateQueries().Object,
            Mock.Of<ILogger<ObjectDownloadsController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        // The model binder yields the FIRST occurrence of a repeated parameter (query or form field):
        // mimic that so the controller sees the same inputs as it would in production.
        var pathsRaw = pathValues.Length > 0 ? pathValues[0] : null;

        Func<Task> invoke = async () => _ = post
            ? await controller.DownloadPost("org", "ds", pathsRaw, null, CancellationToken.None)
            : await controller.Download("org", "ds", pathsRaw, null, CancellationToken.None);

        try
        {
            await invoke();
        }
        catch (DownloadSentinel sentinel)
        {
            return sentinel.Captured;
        }

        Assert.Fail("DownloadStream was never invoked: the controller did not reach the download path.");
        return null!; // unreachable: Assert.Fail throws
    }

    // A long selection is a form with one "path" field per file: each is a literal path, a name may hold a comma
    [Test]
    public async Task RepeatedPathParams_AreTakenLiterally_NoCommaSplit([Values] bool post)
    {
        var paths = await CaptureParsedPaths(post, "ortho & crop.tif", "we,ird,name.tif");
        paths.ShouldBe(["ortho & crop.tif", "we,ird,name.tif"]);
    }

    [Test]
    public async Task RepeatedPathParams_DropEmptyEntries([Values] bool post)
    {
        var paths = await CaptureParsedPaths(post, "", "a.tif");
        paths.ShouldBe(["a.tif"]);
    }

    [Test]
    public async Task SingleOccurrence_KeepsLegacyCommaSplit([Values] bool post)
    {
        // Old shared links encode multiple files in one comma-joined value.
        var paths = await CaptureParsedPaths(post, "a.tif,b.tif");
        paths.ShouldBe(["a.tif", "b.tif"]);
    }

    [Test]
    public async Task SingleOccurrence_AmpersandAndSpacesSurvive([Values] bool post)
    {
        // The '&' from the original tile bug must survive intact on download too.
        var paths = await CaptureParsedPaths(post, "ortho & crop.tif");
        paths.ShouldBe(["ortho & crop.tif"]);
    }

    [Test]
    public async Task NoPathParam_PassesNullPaths([Values] bool post)
    {
        var paths = await CaptureParsedPaths(post);
        paths.ShouldBeNull();
    }
}
