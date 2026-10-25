#nullable enable
using System.Threading;
using Hangfire.Server;

namespace Registry.Web.Services.Jobs;

/// <summary>The cancellation of a job that has no token parameter (the overloads without a token parameter).</summary>
public static class JobCancellation
{
    /// <summary>
    /// The token Hangfire cancels when the server shuts down or the job is aborted (the one it
    /// passes to a <see cref="CancellationToken"/> parameter), or none outside a server.
    /// </summary>
    public static CancellationToken Of(PerformContext? context) =>
        context?.CancellationToken?.ShutdownToken ?? CancellationToken.None;
}
