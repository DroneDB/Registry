using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.NodeOdx;
using Registry.Web.Services.Processing;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// <c>GET /sys/processingNodes[/{id}/options|status]</c> read the nodes from
/// <see cref="NodeOdxNodeRegistry"/>, the ones the photogrammetry tool resolves, instead of their own copy of
/// <c>ProcessingPlatform.NodeOdx</c>. Visible difference, misconfigured nodes only: a node without a URL is not
/// listed (the tool could never use it) and its id is trimmed like the tool's.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~ProcessingNodeCatalogTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public sealed class ProcessingNodeCatalogTests
{
    private readonly Mock<INodeOdxClient> _client = new();

    private ProcessingNodeCatalog Catalog(params NodeOdxNodeConfig[] nodes) =>
        new(new NodeOdxNodeRegistry(Microsoft.Extensions.Options.Options.Create(
                new ProcessingPlatformSettings { NodeOdx = [.. nodes] })), _client.Object,
            NullLogger<ProcessingNodeCatalog>.Instance);

    [Test]
    public void List_IsTheNodesThePhotogrammetryToolCanUse()
    {
        var catalog = Catalog(
            new NodeOdxNodeConfig { Id = " fast ", Url = "http://fast:3000", Title = "Fast node" },
            new NodeOdxNodeConfig { Id = "plain", Url = "http://plain:3000" },
            new NodeOdxNodeConfig { Id = "broken", Url = "" });

        catalog.List().Select(n => (n.Id, n.Title)).ShouldBe([("fast", "Fast node"), ("plain", "plain")]);
    }

    [Test]
    public async Task Status_OfAnUnreachableNode_IsReported_AnUnknownNodeIs404()
    {
        _client.Setup(c => c.GetInfoAsync(It.IsAny<NodeOdxEndpoint>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));
        var catalog = Catalog(new NodeOdxNodeConfig { Id = "n1", Url = "http://n1:3000" });

        var status = await catalog.GetStatusAsync("N1", CancellationToken.None);

        status.Reachable.ShouldBeFalse();
        status.ErrorMessage.ShouldBe("connection refused");
        await Should.ThrowAsync<NotFoundException>(() => catalog.GetStatusAsync("other", CancellationToken.None));
        await Should.ThrowAsync<NotFoundException>(() => catalog.GetOptionsAsync("other", CancellationToken.None));
    }

    [Test]
    public async Task Options_AreReadLiveFromTheNode()
    {
        _client.Setup(c => c.GetOptionsAsync(It.Is<NodeOdxEndpoint>(e => e.Id == "n1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NodeOdxOption("fast-orthophoto", "bool", null, "Skip the point cloud", "false")]);

        var options = await Catalog(new NodeOdxNodeConfig { Id = "n1", Url = "http://n1:3000" })
            .GetOptionsAsync("n1", CancellationToken.None);

        options.ShouldHaveSingleItem().Name.ShouldBe("fast-orthophoto");
    }
}
