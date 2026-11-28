using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Common;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <see cref="BestEffort"/>: a failure of the work goes to the caller's handler and the caller goes on, with the answer
/// the handler gives for a read; a cancellation of the caller's token is thrown, a cancellation the caller did not ask
/// for is a failure like any other.
/// </summary>
[TestFixture]
public class BestEffortTests
{
    [Test]
    public void Get_ReturnsTheValueOfTheRead_WithoutCallingTheHandler()
    {
        var handled = false;

        BestEffort.Get(() => 7, _ =>
        {
            handled = true;
            return -1;
        }).ShouldBe(7);
        handled.ShouldBeFalse();
    }

    [Test]
    public void Get_AFailureGoesToTheHandler_WhoseAnswerIsReturned()
    {
        Exception? handled = null;

        BestEffort.Get<int>(() => throw new IOException("disk"), ex =>
        {
            handled = ex;
            return -1;
        }).ShouldBe(-1);
        handled.ShouldBeOfType<IOException>().Message.ShouldBe("disk");
    }

    [Test]
    public void Run_AFailureGoesToTheHandler()
    {
        Exception? handled = null;

        BestEffort.Run(() => throw new InvalidOperationException("busy"), ex => handled = ex);

        handled.ShouldBeOfType<InvalidOperationException>();
    }

    [Test]
    public void Run_ACancellationOfTheCallersTokenIsThrown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handled = false;

        Should.Throw<OperationCanceledException>(() =>
            BestEffort.Run(() => cts.Token.ThrowIfCancellationRequested(), _ => handled = true, cts.Token));
        handled.ShouldBeFalse();
    }

    [Test]
    public void Run_ACancellationTheCallerDidNotAskForIsAFailure()
    {
        Exception? handled = null;

        BestEffort.Run(() => throw new OperationCanceledException("timeout"), ex => handled = ex, CancellationToken.None);

        handled.ShouldBeOfType<OperationCanceledException>();
    }

    [Test]
    public async Task GetAsync_AFailureGoesToTheHandler_WhoseAnswerIsReturned()
    {
        Exception? handled = null;

        (await BestEffort.GetAsync<string?>(async () =>
        {
            await Task.Yield();
            throw new IOException("gone");
        }, ex =>
        {
            handled = ex;
            return null;
        })).ShouldBeNull();
        handled.ShouldBeOfType<IOException>();
    }

    [Test]
    public async Task RunAsync_RunsTheWork_WithoutCallingTheHandler()
    {
        var ran = false;
        var handled = false;

        await BestEffort.RunAsync(async () =>
        {
            await Task.Yield();
            ran = true;
        }, _ => handled = true);

        ran.ShouldBeTrue();
        handled.ShouldBeFalse();
    }

    [Test]
    public async Task RunAsync_ACancellationOfTheCallersTokenIsThrown()
    {
        using var cts = new CancellationTokenSource();
        var handled = false;

        await Should.ThrowAsync<OperationCanceledException>(() => BestEffort.RunAsync(async () =>
        {
            await cts.CancelAsync();
            cts.Token.ThrowIfCancellationRequested();
        }, _ => handled = true, cts.Token));
        handled.ShouldBeFalse();
    }

    [Test]
    public async Task RunAsync_ACancellationTheCallerDidNotAskForIsAFailure()
    {
        using var cts = new CancellationTokenSource();
        Exception? handled = null;

        await BestEffort.RunAsync(() => Task.FromCanceled(new CancellationToken(canceled: true)), ex => handled = ex,
            cts.Token);

        handled.ShouldBeAssignableTo<OperationCanceledException>();
    }
}
