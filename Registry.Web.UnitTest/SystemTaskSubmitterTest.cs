using System;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Models;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class SystemTaskSubmitterTest
{
    private Mock<IHeavyTool> _tool = null!;
    private Mock<IHeavyToolRegistry> _registry = null!;
    private Mock<IBackgroundJobsProcessor> _processor = null!;
    private IndexPayload? _payload;

    [SetUp]
    public void Setup()
    {
        _tool = new Mock<IHeavyTool>();
        _tool.SetupGet(t => t.Id).Returns("rescan-index");
        _tool.SetupGet(t => t.Version).Returns("1");
        _tool.Setup(t => t.ValidateAsync(It.IsAny<HeavyToolRequest>(), It.IsAny<IHeavyToolValidationContext>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        _registry = new Mock<IHeavyToolRegistry>();
        _registry.Setup(r => r.Resolve("rescan-index", null)).Returns(_tool.Object);

        _payload = null;
        _processor = new Mock<IBackgroundJobsProcessor>();
        _processor
            .Setup(p => p.EnqueueIndexed(It.IsAny<Expression<Func<HeavyTaskJobWrapper, Task>>>(), It.IsAny<IndexPayload>()))
            .Callback<Expression<Func<HeavyTaskJobWrapper, Task>>, IndexPayload>((_, p) => _payload = p)
            .Returns("task-1");
    }

    [Test]
    public async Task SubmitAsync_ValidatesAndEnqueuesAttributedTask()
    {
        var prms = JsonSerializer.SerializeToElement(new { extensions = "dwg" });

        var id = await CreateSubmitter().SubmitAsync("rescan-index", "org", "ds", Mock.Of<IDDB>(), null, prms, "admin");

        id.ShouldBe("task-1");
        _tool.Verify(t => t.ValidateAsync(It.Is<HeavyToolRequest>(r => r.OrgSlug == "org" && r.DsSlug == "ds"),
            It.IsAny<IHeavyToolValidationContext>(), It.IsAny<CancellationToken>()), Times.Once);
        _payload.ShouldNotBeNull();
        _payload.Queue.ShouldBe(JobQueues.Tasks);
        _payload.ToolId.ShouldBe("rescan-index");
        _payload.UserId.ShouldBe("admin");
        _payload.RequestHash.ShouldBe(HeavyTaskRequestHash.Compute("rescan-index", "1", null, prms));
    }

    [Test]
    public async Task SubmitAsync_InvalidRequest_EnqueuesNothing()
    {
        _tool.Setup(t => t.ValidateAsync(It.IsAny<HeavyToolRequest>(), It.IsAny<IHeavyToolValidationContext>(),
            It.IsAny<CancellationToken>())).ThrowsAsync(new ArgumentException("bad"));

        await Should.ThrowAsync<ArgumentException>(() => CreateSubmitter().SubmitAsync("rescan-index", "org", "ds",
            Mock.Of<IDDB>(), null, JsonSerializer.SerializeToElement(new { }), null));
        _payload.ShouldBeNull();
    }

    [Test]
    public async Task SubmitAsync_UnknownTool_Throws()
    {
        await Should.ThrowAsync<HeavyToolNotFoundException>(() => CreateSubmitter().SubmitAsync("nope", "org", "ds",
            Mock.Of<IDDB>(), null, JsonSerializer.SerializeToElement(new { }), null));
    }

    private SystemTaskSubmitter CreateSubmitter() =>
        new(_registry.Object, _processor.Object, NullLogger<SystemTaskSubmitter>.Instance);
}
