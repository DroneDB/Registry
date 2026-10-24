#nullable enable
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Registry.Web.Test.Support;

/// <summary>
/// Bounded wait for state that another process commits and that raises no event the test can
/// await. Its only users are the MySQL-gated worker tests: a Hangfire state change becomes visible
/// when the worker commits its storage transaction, and Hangfire has no hook after that commit
/// (<c>IApplyStateFilter</c> runs before it). Everything that has an event (a job body, a gate, a
/// timer) is awaited directly instead; do not use this for in-process code.
/// </summary>
public static class Eventually
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>Returns once <paramref name="condition"/> holds; fails the test after <paramref name="timeout"/>.</summary>
    public static async Task Until(Func<Task<bool>> condition, string what, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await condition())
        {
            if (elapsed.Elapsed > timeout)
                Assert.Fail($"Timed out after {timeout.TotalSeconds:F0}s waiting for: {what}");
            await Task.Delay(PollInterval);
        }
    }
}
