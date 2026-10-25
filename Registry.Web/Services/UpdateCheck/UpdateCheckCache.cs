#nullable enable
using System;

namespace Registry.Web.Services.UpdateCheck;

/// <summary>A successful update check: what the service answered and when.</summary>
/// <param name="Response">The answer.</param>
/// <param name="CheckedAt">When it was received (UTC).</param>
public sealed record UpdateCheckResult(UpdateCheckResponse Response, DateTime CheckedAt);

/// <summary>
/// What the update check remembers between runs (<c>.update-check-cache.json</c>). It never prevents a request: every run
/// asks again.
/// </summary>
/// <param name="LastResult">The last good answer, kept while later checks fail.</param>
/// <param name="LastFailureAt">When the last check failed (UTC); null after a check that succeeded.</param>
/// <param name="LastNotifiedVersion">The latest version the administrators were e-mailed about.</param>
public sealed record UpdateCheckCache(UpdateCheckResult? LastResult, DateTime? LastFailureAt, string? LastNotifiedVersion)
{
    /// <summary>Nothing remembered yet.</summary>
    public static UpdateCheckCache Empty { get; } = new(null, null, null);
}
