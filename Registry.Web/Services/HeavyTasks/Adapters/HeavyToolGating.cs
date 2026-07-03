#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// Config-driven implementation of <see cref="IHeavyToolGating"/>. Reads the per-tool
/// gating rules from <c>AppSettings:ProcessingPlatform:Tools</c> and resolves the
/// caller's roles through <see cref="IAuthManager"/>.
/// </summary>
public sealed class HeavyToolGating : IHeavyToolGating
{
    private const string AdminRole = "admin";

    private readonly ProcessingPlatformSettings _settings;
    private readonly IAuthManager _authManager;

    private static readonly HeavyToolConfig DefaultConfig = new();

    public HeavyToolGating(IOptions<AppSettings> appSettings, IAuthManager authManager)
    {
        _settings = appSettings.Value.ProcessingPlatform ?? new ProcessingPlatformSettings();
        _authManager = authManager;
    }

    public HeavyToolConfig GetConfig(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId) || _settings.Tools.Count == 0)
            return DefaultConfig;

        // Tool ids are kebab-case; match case-insensitively for robustness.
        foreach (var kv in _settings.Tools)
        {
            if (kv.Key.Equals(toolId, StringComparison.OrdinalIgnoreCase))
                return kv.Value ?? DefaultConfig;
        }

        return DefaultConfig;
    }

    public async Task<HeavyToolState> EvaluateAsync(string toolId, string? orgSlug)
    {
        var cfg = GetConfig(toolId);

        switch (cfg.Availability)
        {
            // Step 2: globally hidden.
            case HeavyToolAvailability.Hidden:
                return new HeavyToolState(Hidden: true, Disabled: false, DisabledMessage: null);
            // Step 3: globally disabled.
            case HeavyToolAvailability.Disabled:
                return new HeavyToolState(Hidden: false, Disabled: true,
                    DisabledMessage: cfg.DisabledMessage ?? "This tool is currently disabled.");
        }

        // Step 4: role allowlist.
        if (cfg.AllowedRoles.Count > 0 && !await IsCallerInAnyRoleAsync(cfg.AllowedRoles))
            return DenyByAllowlist(cfg,
                cfg.DisabledMessage ?? "You do not have the required role to use this tool.");

        // Step 5: organization allowlist (only when an org context is available).
        if (cfg.AllowedOrgs.Count > 0 && orgSlug != null &&
            !cfg.AllowedOrgs.Any(o => o.Equals(orgSlug, StringComparison.OrdinalIgnoreCase)))
            return DenyByAllowlist(cfg,
                cfg.DisabledMessage ?? "This tool is not available for this organization.");

        // Step 6: fully available.
        return HeavyToolState.Enabled;
    }

    private async Task<bool> IsCallerInAnyRoleAsync(IEnumerable<string> roles)
    {
        foreach (var role in roles)
        {
            if (string.IsNullOrWhiteSpace(role))
                continue;

            if (role.Equals(AdminRole, StringComparison.OrdinalIgnoreCase))
            {
                if (await _authManager.IsUserAdmin())
                    return true;
            }
            else if (await _authManager.IsUserInRole(role))
            {
                return true;
            }
        }

        return false;
    }

    private static HeavyToolState DenyByAllowlist(HeavyToolConfig cfg, string message)
    {
        return cfg.HideWhenNotAllowed
            ? new HeavyToolState(Hidden: true, Disabled: false, DisabledMessage: null)
            : new HeavyToolState(Hidden: false, Disabled: true, DisabledMessage: message);
    }
}
