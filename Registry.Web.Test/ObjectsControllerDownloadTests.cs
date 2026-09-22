using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MSOptions = Microsoft.Extensions.Options.Options;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Controllers;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Regression tests for the GET Download path parsing: with multiple "path"
/// query occurrences every value is taken verbatim (file names may contain
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

    private static Mock<IObjectsManager> CreateManager()
    {
        var manager = new Mock<IObjectsManager>(MockBehavior.Strict);

        // Everything is treated as a directory so even single-path requests
        // fall through to DownloadStream, where the parsed array is captured.
        manager.Setup(m => m.GetEntryType(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string _, string __, string ___) => Task.FromResult<EntryType?>(EntryType.Directory));

        manager.Setup(m => m.DownloadStream(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>() ))
            .Callback((string _, string __, string[] paths) => throw new DownloadSentinel(paths));

        return manager;
    }

    private static async Task<string[]> CaptureParsedPaths(string[] queryPathValues)
    {
        var httpContext = new DefaultHttpContext
        {
            Request =
            {
                Query = new QueryCollection(new Dictionary<string, StringValues>
                {
                    ["path"] = new StringValues(queryPathValues)
                })
            }
        };

        var controller = new ObjectsController(
            CreateManager().Object,
            Mock.Of<ILogger<ObjectsController>>(),
            MSOptions.Create(new FormOptions()),
            MSOptions.Create(new AppSettings()))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        // The [FromQuery] string binding only ever yields the FIRST occurrence
        // of a repeated parameter: mimic that so the controller sees the same
        // inputs as it would in production.
        var pathsRaw = queryPathValues.Length > 0 ? queryPathValues[0] : null;

        Func<Task> invoke = async () => await controller.Download("org", "ds", pathsRaw, null, CancellationToken.None);

        try
        {
            await invoke();
        }
        catch (DownloadSentinel sentinel)
        {
            return sentinel.Captured;
        }

        Assert.Fail("DownloadStream was never invoked: the controller did not reach the download path.");
        return null;
    }

    [Test]
    public async Task RepeatedPathParams_AreTakenLiterally_NoCommaSplit()
    {
        var paths = await CaptureParsedPaths(new[] { "ortho & crop.tif", "we,ird,name.tif" });
        paths.ShouldBe(new[] { "ortho & crop.tif", "we,ird,name.tif" });
    }

    [Test]
    public async Task RepeatedPathParams_DropEmptyEntries()
    {
        var paths = await CaptureParsedPaths(new[] { "", "a.tif" });
        paths.ShouldBe(new[] { "a.tif" });
    }

    [Test]
    public async Task SingleOccurrence_KeepsLegacyCommaSplit()
    {
        // Old shared links encode multiple files in one comma-joined value.
        var paths = await CaptureParsedPaths(new[] { "a.tif,b.tif" });
        paths.ShouldBe(new[] { "a.tif", "b.tif" });
    }

    [Test]
    public async Task SingleOccurrence_AmpersandAndSpacesSurvive()
    {
        // The '&' from the original tile bug must survive intact on download too.
        var paths = await CaptureParsedPaths(new[] { "ortho & crop.tif" });
        paths.ShouldBe(new[] { "ortho & crop.tif" });
    }

    [Test]
    public async Task NoPathParam_PassesNullPaths()
    {
        var paths = await CaptureParsedPaths(Array.Empty<string>());
        paths.ShouldBeNull();
    }
}
