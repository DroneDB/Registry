#nullable enable
using System;

namespace Registry.Web.Utilities;

/// <summary>
/// The graceful-shutdown budget (RQ-02 item 6, XI-07). The stages nest, each inside the next:
/// <list type="number">
/// <item>the Hangfire servers drain their jobs (<see cref="HangfireServer"/>);</item>
/// <item>then libddb shuts down (<c>DDBShutdown</c>: checkpoints and closes the index connections),
/// waiting at most <see cref="Ddb"/> for calls still in flight; it runs in the DDB runtime service's
/// <c>StoppedAsync</c>, after every hosted service stopped;</item>
/// <item>the host gives the whole stop <see cref="Host"/> (<c>HostOptions.ShutdownTimeout</c>), more
/// than the two stages above;</item>
/// <item>Docker waits <c>stop_grace_period</c> (the compose files) before it kills the container,
/// more than <see cref="Host"/>: <see cref="ComposeStopGracePeriod"/>.</item>
/// </list>
/// A test checks the nesting and the compose files.
/// </summary>
public static class ShutdownTimeouts
{
    /// <summary><c>BackgroundJobServerOptions.ShutdownTimeout</c> of every Hangfire server.</summary>
    public static readonly TimeSpan HangfireServer = TimeSpan.FromSeconds(15);

    /// <summary>How long <c>DDBShutdown</c> waits for libddb calls still running.</summary>
    public static readonly TimeSpan Ddb = TimeSpan.FromSeconds(10);

    /// <summary><c>HostOptions.ShutdownTimeout</c>: the Hangfire drain, the libddb shutdown and a margin.</summary>
    public static readonly TimeSpan Host = TimeSpan.FromSeconds(30);

    /// <summary>The <c>stop_grace_period</c> of the Registry services in the compose files.</summary>
    public static readonly TimeSpan ComposeStopGracePeriod = TimeSpan.FromSeconds(40);
}
