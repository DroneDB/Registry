using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.NodeOdm;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class NodeOdmTests
{
    #region Parsing

    [Test]
    public void ParseInfo_ReadsFields()
    {
        const string json = """
            {"version":"3.5.3","taskQueueCount":2,"maxParallelTasks":4,"engine":"odm","engineVersion":"3.5.3"}
            """;

        var info = NodeOdmClient.ParseInfo(json);

        info.Version.ShouldBe("3.5.3");
        info.TaskQueueCount.ShouldBe(2);
        info.MaxParallelTasks.ShouldBe(4);
        info.Engine.ShouldBe("odm");
        info.EngineVersion.ShouldBe("3.5.3");
    }

    [Test]
    public void ParseNewTaskUuid_ReturnsUuid()
    {
        NodeOdmClient.ParseNewTaskUuid("""{"uuid":"abc-123"}""").ShouldBe("abc-123");
    }

    [Test]
    public void ParseNewTaskUuid_ThrowsOnError()
    {
        Should.Throw<InvalidOperationException>(
            () => NodeOdmClient.ParseNewTaskUuid("""{"error":"Cannot create task"}"""));
    }

    [Test]
    public void ParseNewTaskUuid_ThrowsWhenMissing()
    {
        Should.Throw<InvalidOperationException>(() => NodeOdmClient.ParseNewTaskUuid("""{}"""));
    }

    [TestCase(10, NodeOdmTaskStatusCode.Queued)]
    [TestCase(20, NodeOdmTaskStatusCode.Running)]
    [TestCase(30, NodeOdmTaskStatusCode.Failed)]
    [TestCase(40, NodeOdmTaskStatusCode.Completed)]
    [TestCase(50, NodeOdmTaskStatusCode.Canceled)]
    public void ParseTaskInfo_MapsStatusCode(int code, NodeOdmTaskStatusCode expected)
    {
        var json = $$"""
            {"uuid":"u1","status":{"code":{{code}}},"progress":42.5,"imagesCount":12}
            """;

        var info = NodeOdmClient.ParseTaskInfo(json, "u1");

        info.StatusCode.ShouldBe(expected);
        info.Progress.ShouldBe(42.5);
        info.ImagesCount.ShouldBe(12);
        info.Uuid.ShouldBe("u1");
    }

    [Test]
    public void ParseTaskInfo_ReadsErrorMessage()
    {
        const string json = """
            {"uuid":"u1","status":{"code":30,"errorMessage":"boom"},"progress":0}
            """;

        var info = NodeOdmClient.ParseTaskInfo(json, "u1");

        info.StatusCode.ShouldBe(NodeOdmTaskStatusCode.Failed);
        info.ErrorMessage.ShouldBe("boom");
    }

    [Test]
    public void ParseTaskInfo_TopLevelErrorBecomesFailed()
    {
        var info = NodeOdmClient.ParseTaskInfo("""{"error":"not found"}""", "u1");

        info.StatusCode.ShouldBe(NodeOdmTaskStatusCode.Failed);
        info.ErrorMessage.ShouldBe("not found");
    }

    [Test]
    public void ParseOutput_ReadsLines()
    {
        var lines = NodeOdmClient.ParseOutput("""["line 1","line 2","line 3"]""");

        lines.Count.ShouldBe(3);
        lines[0].ShouldBe("line 1");
        lines[2].ShouldBe("line 3");
    }

    [Test]
    public void ParseOutput_EmptyArray()
    {
        NodeOdmClient.ParseOutput("[]").Count.ShouldBe(0);
    }

    #endregion

    #region Node registry

    private static IOptions<AppSettings> Settings(params NodeOdmNodeConfig[] nodes)
    {
        var s = new AppSettings
        {
            ProcessingPlatform = new ProcessingPlatformSettings
            {
                NodeOdm = nodes.ToList()
            }
        };
        return Microsoft.Extensions.Options.Options.Create(s);
    }

    [Test]
    public void Registry_ResolveByNull_ReturnsFirst()
    {
        var reg = new NodeOdmNodeRegistry(Settings(
            new NodeOdmNodeConfig { Id = "a", Url = "http://a:3000" },
            new NodeOdmNodeConfig { Id = "b", Url = "http://b:3000" }));

        reg.HasNodes.ShouldBeTrue();
        reg.Resolve()!.Id.ShouldBe("a");
    }

    [Test]
    public void Registry_ResolveById()
    {
        var reg = new NodeOdmNodeRegistry(Settings(
            new NodeOdmNodeConfig { Id = "a", Url = "http://a:3000" },
            new NodeOdmNodeConfig { Id = "b", Url = "http://b:3000", Token = "t" }));

        var node = reg.Resolve("b");
        node!.Url.ShouldBe("http://b:3000");
        node.Token.ShouldBe("t");
    }

    [Test]
    public void Registry_ResolveUnknown_ReturnsNull()
    {
        var reg = new NodeOdmNodeRegistry(Settings(
            new NodeOdmNodeConfig { Id = "a", Url = "http://a:3000" }));

        reg.Resolve("nope").ShouldBeNull();
    }

    [Test]
    public void Registry_EmptyConfig_HasNoNodes()
    {
        var reg = new NodeOdmNodeRegistry(Settings());

        reg.HasNodes.ShouldBeFalse();
        reg.Resolve().ShouldBeNull();
    }

    [Test]
    public void Registry_SkipsNodesWithoutUrl()
    {
        var reg = new NodeOdmNodeRegistry(Settings(
            new NodeOdmNodeConfig { Id = "blank", Url = "" },
            new NodeOdmNodeConfig { Id = "ok", Url = "http://ok:3000" }));

        reg.All.Count.ShouldBe(1);
        reg.Resolve()!.Id.ShouldBe("ok");
    }

    #endregion

    #region Client over mocked HTTP

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<HttpRequestMessage> Requests { get; } = new();

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static NodeOdmClient ClientFor(HttpMessageHandler handler) =>
        new(new StubFactory(handler),
            Microsoft.Extensions.Options.Options.Create(new AppSettings { ProcessingPlatform = new ProcessingPlatformSettings() }),
            NullLogger<NodeOdmClient>.Instance);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Test]
    public async Task GetInfoAsync_IncludesTokenInQuery()
    {
        var handler = new StubHandler(_ => Json("""{"version":"3.5.3","taskQueueCount":0,"maxParallelTasks":1}"""));
        var client = ClientFor(handler);
        var node = new NodeOdmEndpoint("n", "http://node:3000", "secret", null);

        var info = await client.GetInfoAsync(node, CancellationToken.None);

        info.Version.ShouldBe("3.5.3");
        handler.Requests[0].RequestUri!.ToString().ShouldContain("token=secret");
        handler.Requests[0].RequestUri!.AbsolutePath.ShouldBe("/info");
    }

    [Test]
    public async Task GetTaskOutputAsync_SendsLineCursor()
    {
        var handler = new StubHandler(_ => Json("""["a","b"]"""));
        var client = ClientFor(handler);
        var node = new NodeOdmEndpoint("n", "http://node:3000", null, null);

        var lines = await client.GetTaskOutputAsync(node, "uuid-1", 5, CancellationToken.None);

        lines.Count.ShouldBe(2);
        handler.Requests[0].RequestUri!.Query.ShouldContain("line=5");
    }

    [Test]
    public async Task CreateTaskAsync_PostsMultipartAndReturnsUuid()
    {
        var tempImg = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempImg, "fake-image");
        try
        {
            var handler = new StubHandler(_ => Json("""{"uuid":"task-xyz"}"""));
            var client = ClientFor(handler);
            var node = new NodeOdmEndpoint("n", "http://node:3000", null, null);

            var uuid = await client.CreateTaskAsync(node, "my-task", new[] { tempImg }, "[]", CancellationToken.None);

            uuid.ShouldBe("task-xyz");
            handler.Requests[0].Method.ShouldBe(HttpMethod.Post);
            handler.Requests[0].RequestUri!.AbsolutePath.ShouldBe("/task/new");
        }
        finally
        {
            File.Delete(tempImg);
        }
    }

    [Test]
    public async Task DownloadAssetAsync_WritesFile()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
        });
        var client = ClientFor(handler);
        var node = new NodeOdmEndpoint("n", "http://node:3000", null, null);
        var dest = Path.GetTempFileName();
        try
        {
            await client.DownloadAssetAsync(node, "uuid-1", "all.zip", dest, CancellationToken.None);
            (await File.ReadAllBytesAsync(dest)).Length.ShouldBe(4);
        }
        finally
        {
            File.Delete(dest);
        }
    }

    #endregion
}
