#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;
using Registry.Web.Controllers;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>Server side: task change notifications, the event stream, and 304 on an unchanged list.</summary>
[TestFixture]
public class TaskEventsTests
{
    [Test]
    public void Notifier_CallsTheListenersOfTheDatasetOnly_UntilDisposed()
    {
        var notifier = new InMemoryTaskChangeNotifier();
        var calls = 0;
        var subscription = notifier.Subscribe("org", "ds", () => calls++);

        notifier.Publish("org", "other");
        notifier.Publish("org", "ds");
        calls.ShouldBe(1);

        subscription.Dispose();
        notifier.Publish("org", "ds");
        calls.ShouldBe(1);
    }

    private static TasksController Controller(Mock<ITasksManager> manager, HttpContext context) =>
        new(manager.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

    [Test]
    public async Task List_AnswersNotModified_WhenTheClientHasThePage()
    {
        var manager = new Mock<ITasksManager>();
        manager.Setup(m => m.ListPageAsync("org", "ds", null, null, 0, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaskListPage([], "W/\"abc-50\""));

        var first = new DefaultHttpContext();
        (await Controller(manager, first).List("org", "ds", null, null)).ShouldBeOfType<OkObjectResult>();
        first.Response.Headers.ETag.ToString().ShouldBe("W/\"abc-50\"");

        var again = new DefaultHttpContext();
        again.Request.Headers.IfNoneMatch = "W/\"abc-50\"";
        (await Controller(manager, again).List("org", "ds", null, null))
            .ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(StatusCodes.Status304NotModified);
    }

    [Test]
    public async Task Events_StreamsATasksEventOnEveryChange()
    {
        var notifier = new InMemoryTaskChangeNotifier();
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new Mock<ITasksManager>();
        manager.Setup(m => m.SubscribeAsync("org", "ds", It.IsAny<Action>()))
            .Returns((string o, string d, Action a) =>
            {
                var subscription = notifier.Subscribe(o, d, a);
                subscribed.SetResult();
                return Task.FromResult(subscription);
            });

        var context = new DefaultHttpContext();
        var body = new ObservedStream("event: tasks\n");
        context.Response.Body = body;
        using var cts = new CancellationTokenSource();

        var stream = Controller(manager, context).Events("org", "ds", cts.Token);
        await subscribed.Task;
        notifier.Publish("org", "ds");
        await body.MarkerWritten;
        await cts.CancelAsync();
        await stream;

        var text = Encoding.UTF8.GetString(body.ToArray());
        text.ShouldStartWith("retry: 5000\n\n");
        text.ShouldContain("event: tasks\n");
        context.Response.ContentType.ShouldBe("text/event-stream");
        context.Response.Headers["X-Accel-Buffering"].ToString().ShouldBe("no");
    }

    /// <summary>Response body that completes <see cref="MarkerWritten"/> once the marker has been written.</summary>
    private sealed class ObservedStream(string marker) : MemoryStream
    {
        private readonly TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task MarkerWritten => _written.Task;

        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, count);
            Observe();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            Observe();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private void Observe()
        {
            if (Encoding.UTF8.GetString(ToArray()).Contains(marker, StringComparison.Ordinal))
                _written.TrySetResult();
        }
    }
}
