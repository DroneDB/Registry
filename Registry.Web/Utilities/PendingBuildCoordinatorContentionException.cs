using System;

namespace Registry.Web.Utilities;

/// <summary>
/// A dataset's pending-build coordinator is currently owned by another job.
/// Only this distributed-lock timeout receives coordinator contention retries.
/// </summary>
public sealed class PendingBuildCoordinatorContentionException : Exception
{
    /// <summary>
    /// Creates a contention failure for the affected dataset.
    /// </summary>
    /// <param name="orgSlug">Organization identity.</param>
    /// <param name="dsSlug">Dataset identity.</param>
    /// <param name="innerException">Storage lock-acquisition timeout.</param>
    public PendingBuildCoordinatorContentionException(string orgSlug, string dsSlug, Exception innerException)
        : base($"Pending-build coordination for '{orgSlug}/{dsSlug}' is busy", innerException)
    {
    }
}
