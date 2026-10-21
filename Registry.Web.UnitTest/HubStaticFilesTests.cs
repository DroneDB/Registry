#nullable enable
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Web.Middlewares;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>HUB-09: cache policy of the Hub's static files and serving of build-time compressed twins.</summary>
[TestFixture]
public class HubStaticFilesTests
{
    [TestCase("main.190b8a1b.js", HubCachePolicy.Immutable)]
    [TestCase("vendor-3d.3237e08f.css", HubCachePolicy.Immutable)]
    [TestCase("2463b90d9a316e4e5294.woff2", HubCachePolicy.Immutable)]
    [TestCase("1815e00441357e01619e.ttf", HubCachePolicy.Immutable)]
    [TestCase("index.html", HubCachePolicy.Document)]
    [TestCase("/INDEX.HTML", HubCachePolicy.Document)]
    [TestCase("potree/build/potree/potree.js", HubCachePolicy.Public)]
    [TestCase("main.js", HubCachePolicy.Public)]
    [TestCase("wasm/laz-perf.wasm", HubCachePolicy.Public)]
    [TestCase("version.txt", HubCachePolicy.Public)]
    [TestCase("images/logo.190b8a1b.png", HubCachePolicy.Public)] // only webpack's own patterns are trusted
    public void CachePolicy_FollowsTheFileName(string file, string expected) =>
        HubCachePolicy.For(file).ShouldBe(expected);

    [TestCase("potree/libs/three.js/build/three.min.js", HubCachePolicy.Immutable)]
    [TestCase("potree/build/potree/potree.css", HubCachePolicy.Immutable)]
    [TestCase("index.html", HubCachePolicy.Document)]
    public void CachePolicy_VersionedUrlsAreImmutable_ButNeverTheDocument(string file, string expected) =>
        HubCachePolicy.For(file, versioned: true).ShouldBe(expected);

    private static (string Root, IFileProvider Files) BuildFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "hub-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "main.12345678.js"), "js");
        File.WriteAllText(Path.Combine(root, "main.12345678.js.br"), "br");
        File.WriteAllText(Path.Combine(root, "main.12345678.js.gz"), "gz");
        File.WriteAllText(Path.Combine(root, "plain.12345678.css"), "css");
        return (root, new PhysicalFileProvider(root));
    }

    private static async Task<HttpContext> Run(IFileProvider files, string path, string acceptEncoding)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Request.Headers.AcceptEncoding = acceptEncoding;
        await new PrecompressedStaticFilesMiddleware(_ => Task.CompletedTask, () => files).InvokeAsync(context);
        return context;
    }

    [Test]
    public async Task BrotliTwin_IsServedWithTheOriginalContentType()
    {
        var (root, files) = BuildFolder();
        try
        {
            var context = await Run(files, "/main.12345678.js", "gzip, deflate, br");

            context.Request.Path.Value.ShouldBe("/main.12345678.js.br");
            context.Response.Headers.ContentEncoding.ToString().ShouldBe("br");
            context.Response.Headers.Vary.ToString().ShouldContain("Accept-Encoding");
            PrecompressedStaticFilesMiddleware.OriginalContentType(context).ShouldBe("text/javascript");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task GzipTwin_WhenBrotliIsNotAccepted_AndNothingWithoutATwinOrAnEncoding()
    {
        var (root, files) = BuildFolder();
        try
        {
            (await Run(files, "/main.12345678.js", "gzip")).Request.Path.Value.ShouldBe("/main.12345678.js.gz");

            var identity = await Run(files, "/main.12345678.js", "");
            identity.Request.Path.Value.ShouldBe("/main.12345678.js");
            identity.Response.Headers.ContentEncoding.ToString().ShouldBeEmpty();

            (await Run(files, "/plain.12345678.css", "br")).Request.Path.Value.ShouldBe("/plain.12345678.css");
            (await Run(files, "/main.12345678.js", "br;q=0, gzip")).Request.Path.Value.ShouldBe("/main.12345678.js.gz");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // What the static files middleware sends for a path, with the Hub's options
    private static async Task<HttpContext> Serve(IFileProvider files, string path, string acceptEncoding = "")
    {
        var options = HubCachePolicy.StaticFileOptions();
        options.FileProvider = files;
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootFileProvider).Returns(files);
        var staticFiles = new StaticFileMiddleware(_ => Task.CompletedTask, env.Object, Microsoft.Extensions.Options.Options.Create(options),
            NullLoggerFactory.Instance);

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Request.Headers.AcceptEncoding = acceptEncoding;
        context.Response.Body = new MemoryStream();
        await new PrecompressedStaticFilesMiddleware(staticFiles.Invoke, () => files).InvokeAsync(context);
        return context;
    }

    [Test]
    public async Task IndexHtml_IsAlwaysRevalidated_HashedFilesAndTheirTwinsAreImmutable()
    {
        var (root, files) = BuildFolder();
        File.WriteAllText(Path.Combine(root, "index.html"), "<html></html>");
        try
        {
            // Also what client routes (/login, /r/...) get: Startup passes the same options to the
            // SPA default page, which serves index.html through them
            var index = await Serve(files, "/index.html");
            index.Response.Headers.CacheControl.ToString().ShouldBe(HubCachePolicy.Document);
            index.Response.Headers.Pragma.ToString().ShouldBe("no-cache");

            var twin = await Serve(files, "/main.12345678.js", "br");
            twin.Response.Headers.CacheControl.ToString().ShouldBe(HubCachePolicy.Immutable);
            twin.Response.Headers.ContentEncoding.ToString().ShouldBe("br");
            twin.Response.ContentType.ShouldBe("text/javascript");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
