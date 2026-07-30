#nullable enable
using System.Threading.Tasks;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Services.HeavyTasks.Ports;

/// <summary>
/// Computes the effective feature-gating state of heavy tools for the current caller.
/// Implementations depend on the request-scoped <c>IAuthManager</c> and must be
/// registered with a Scoped lifetime.
/// </summary>
public interface IHeavyToolGating
{
    /// <summary>
    /// Returns the per-tool configuration for the given tool id, applying defaults
    /// (Enabled, no restrictions) when the tool is not explicitly configured.
    /// </summary>
    HeavyToolConfig GetConfig(string toolId);

    /// <summary>
    /// Computes the effective state of a tool for the current caller. Pass
    /// <paramref name="orgSlug"/> = <c>null</c> for the global (features endpoint)
    /// evaluation, which skips the per-organization allowlist check.
    /// </summary>
    Task<HeavyToolState> EvaluateAsync(string toolId, string? orgSlug);
}

/// <summary>
/// Effective gating state of a heavy tool for the current caller.
/// </summary>
/// <param name="Hidden">The tool must not be shown in the UI.</param>
/// <param name="Disabled">The tool must be shown greyed out and non-clickable.</param>
/// <param name="DisabledMessage">Tooltip / rejection reason (present when Disabled).</param>
public sealed record HeavyToolState(bool Hidden, bool Disabled, string? DisabledMessage)
{
    /// <summary>The tool is available (neither hidden nor disabled).</summary>
    public bool Allowed => !Hidden && !Disabled;

    /// <summary>Convenience instance for a fully available tool.</summary>
    public static readonly HeavyToolState Enabled = new(false, false, null);
}
