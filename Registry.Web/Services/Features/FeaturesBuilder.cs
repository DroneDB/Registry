#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Ports.Archives;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Hub;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Features;

/// <summary>The platform features the Hub adapts to (<c>GET /sys/features</c>, anonymous).</summary>
public interface IFeaturesBuilder
{
    Task<FeaturesDto> BuildAsync(CancellationToken ct = default);
}

/// <summary>
/// One part of <see cref="FeaturesDto"/>, from one concern's settings and services: a new feature is a new section,
/// not one more dependency of a builder (RQ-12).
/// </summary>
public interface IFeatureSection
{
    Task FillAsync(FeaturesDto features);
}

/// <summary>Default <see cref="IFeaturesBuilder"/>: every <see cref="IFeatureSection"/> fills its part.</summary>
public sealed class FeaturesBuilder : IFeaturesBuilder
{
    private readonly IReadOnlyList<IFeatureSection> _sections;

    public FeaturesBuilder(IEnumerable<IFeatureSection> sections) => _sections = [.. sections];

    public async Task<FeaturesDto> BuildAsync(CancellationToken ct = default)
    {
        var features = new FeaturesDto();
        foreach (var section in _sections)
        {
            ct.ThrowIfCancellationRequested();
            await section.FillAsync(features);
        }

        return features;
    }
}

/// <summary>Accounts: member and user management, the storage limiter, the password policy.</summary>
public sealed class AccountFeatures : IFeatureSection
{
    private readonly AccountOptions _accounts;
    private readonly IdentityProviderOptions _identity;
    private readonly ILoginManager _loginManager;

    public AccountFeatures(IOptions<AccountOptions> accounts, IOptions<IdentityProviderOptions> identity,
        ILoginManager loginManager)
    {
        _accounts = accounts.Value;
        _identity = identity.Value;
        _loginManager = loginManager;
    }

    public Task FillAsync(FeaturesDto features)
    {
        features.OrganizationMemberManagement = _accounts.EnableOrganizationMemberManagement;
        // The active provider's capability: LDAP, Remote and any external provider report false
        features.UserManagement = _loginManager.Capabilities.SupportsLocalUserManagement;
        features.StorageLimiter = _accounts.EnableStorageLimiter;
        features.PasswordPolicy = _identity.PasswordPolicy is { } policy
            ? new PasswordPolicyDto
            {
                MinLength = policy.MinLength,
                RequireDigit = policy.RequireDigit,
                RequireUppercase = policy.RequireUppercase,
                RequireLowercase = policy.RequireLowercase,
                RequireNonAlphanumeric = policy.RequireNonAlphanumeric
            }
            : null;
        return Task.CompletedTask;
    }
}

/// <summary>Downloads and exports: the limits, the bulk download threshold, the dataset thumbnail candidates.</summary>
public sealed class DownloadFeatures : IFeatureSection
{
    private readonly DownloadOptions _downloads;
    private readonly ThumbnailOptions _thumbnails;
    private readonly ProcessingPlatformSettings _platform;

    public DownloadFeatures(IOptions<DownloadOptions> downloads, IOptions<ThumbnailOptions> thumbnails,
        IOptions<ProcessingPlatformSettings> platform)
    {
        _downloads = downloads.Value;
        _thumbnails = thumbnails.Value;
        _platform = platform.Value;
    }

    public Task FillAsync(FeaturesDto features)
    {
        features.MaxConcurrentDownloadsPerUser = _downloads.MaxConcurrentDownloadsPerUser;
        features.DisableAnonymousBulkDownloads = _downloads.DisableAnonymousBulkDownloads;
        features.MaxExportSizeBytes = _downloads.MaxExportSizeBytes;
        features.BulkDownloadAsyncThresholdBytes = _platform.BulkDownloadAsyncThresholdBytes;
        features.DatasetThumbnailCandidates = _thumbnails.Candidates;
        return Task.CompletedTask;
    }
}

/// <summary>Tasks: the tools with their gating for the caller, and the task states.</summary>
public sealed class TaskFeatures : IFeatureSection
{
    private readonly IHeavyToolRegistry _tools;
    private readonly IHeavyToolGating _gating;

    public TaskFeatures(IHeavyToolRegistry tools, IHeavyToolGating gating)
    {
        _tools = tools;
        _gating = gating;
    }

    public async Task FillAsync(FeaturesDto features)
    {
        // No organization here: the organization allow-list applies on the org-scoped tools endpoint
        var states = await Task.WhenAll(_tools.All.Select(t => _gating.EvaluateAsync(t.Id, orgSlug: null)));

        features.TaskTools =
        [
            .. _tools.All.Zip(states, (t, st) => new TaskToolInfoDto(t.Id, t.Version, t.Title,
                t.RequiredAccess.ToString(), t.ProducesArtifact, t.ResultExtension, st.Hidden, st.Disabled,
                st.DisabledMessage))
        ];
        features.TaskStates = [.. TaskStateCatalog.All.Select(s => new TaskStateInfoDto(s, TaskStateCatalog.IsTerminal(s)))];
    }
}

/// <summary>The Hub: its options and the versions of the Hub, Registry and libddb.</summary>
public sealed class HubFeatures : IFeatureSection
{
    private readonly HubOptions _hub;
    private readonly IDdbWrapper _ddbWrapper;
    private readonly ILogger<HubFeatures> _logger;

    public HubFeatures(IOptions<HubOptions> hub, IDdbWrapper ddbWrapper, ILogger<HubFeatures> logger)
    {
        _hub = hub.Value;
        _ddbWrapper = ddbWrapper;
        _logger = logger;
    }

    public Task FillAsync(FeaturesDto features)
    {
        features.HubOptions = _hub;
        features.HubVersion = HubInfo.CurrentVersion;
        features.RegistryVersion = RegistryVersion.Current;
        features.DdbVersion = DdbVersion();
        return Task.CompletedTask;
    }

    // libddb answers "<semver> <commit>": the semver only
    private string? DdbVersion()
    {
        try
        {
            var raw = _ddbWrapper.GetVersion();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var space = raw.IndexOf(' ', StringComparison.Ordinal);
            return space < 0 ? raw : raw[..space];
        }
        catch (Exception ex)
        {
            // The Hub reads the features when it starts: the version is not worth failing them
            _logger.LogWarning(ex, "Cannot read the libddb version");
            return null;
        }
    }
}

/// <summary>The entry types: what libddb reports (<c>DDBGetEntryTypes</c>), passed through (plan decision D8).</summary>
public sealed class EntryTypeFeatures : IFeatureSection
{
    private readonly IBuildArtifactResolver _builds;

    public EntryTypeFeatures(IBuildArtifactResolver builds) => _builds = builds;

    public Task FillAsync(FeaturesDto features)
    {
        features.EntryTypes =
        [
            .. _builds.EntryTypes.Select(t => new EntryTypeDto((int)t.Id, t.Name, t.Buildable, t.Thumbnail,
                [.. t.SidecarExtensions], [.. t.Roles]))
        ];
        return Task.CompletedTask;
    }
}

/// <summary>The archives Registry extracts into a dataset: the extractor's own rule, not a copy of it.</summary>
public sealed class ArchiveFeatures : IFeatureSection
{
    private readonly IArchiveExtractor _archives;

    public ArchiveFeatures(IArchiveExtractor archives) => _archives = archives;

    public Task FillAsync(FeaturesDto features)
    {
        features.ArchiveExtensions = [.. _archives.SupportedExtensions.Select(e => e.TrimStart('.'))];
        return Task.CompletedTask;
    }
}
