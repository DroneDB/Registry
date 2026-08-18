using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Hub;
using Registry.Web.Services.Ports;
using Registry.Web.Services.HeavyTasks.NodeOdx;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// Controller for system administration and maintenance operations.
/// </summary>
[Authorize]
[ApiController]
[Route(RoutesHelper.SystemRadix)]
[Produces("application/json")]
public class SystemController : ControllerBaseEx
{
    private readonly ISystemManager _systemManager;
    private readonly IDatasetsManager _datasetsManager;
    private readonly IOrganizationsManager _organizationsManager;
    private readonly IDdbWrapper _ddbWrapper;
    private readonly IHeavyToolRegistry _toolRegistry;
    private readonly IHeavyToolGating _toolGating;
    private readonly ILogger<SystemController> _logger;
    private readonly AppSettings _appSettings;
    private readonly ILoginManager _loginManager;
    private readonly IConfigurationDataBuilder _configurationBuilder;
    private readonly INodeOdxClient _nodeOdxClient;

    public SystemController(
        ISystemManager systemManager,
        IDatasetsManager datasetsManager,
        IOrganizationsManager organizationsManager,
        IDdbWrapper ddbWrapper,
        IHeavyToolRegistry toolRegistry,
        IHeavyToolGating toolGating,
        ILogger<SystemController> logger,
        IOptions<AppSettings> appSettings,
        ILoginManager loginManager,
        IConfigurationDataBuilder configurationBuilder,
        INodeOdxClient nodeOdxClient)
    {
        _systemManager = systemManager;
        _datasetsManager = datasetsManager;
        _organizationsManager = organizationsManager;
        _ddbWrapper = ddbWrapper;
        _toolRegistry = toolRegistry;
        _toolGating = toolGating;
        _logger = logger;
        _appSettings = appSettings.Value;
        _loginManager = loginManager;
        _configurationBuilder = configurationBuilder;
        _nodeOdxClient = nodeOdxClient;
    }

    /// <summary>
    /// Gets the current version of the Registry system.
    /// </summary>
    /// <returns>The version string.</returns>
    [HttpGet("version", Name = nameof(SystemController) + "." + nameof(GetVersion))]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public IActionResult GetVersion()
    {
        try
        {
            _logger.LogDebug("System controller GetVersion()");

            return Ok(_systemManager.GetVersion());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller GetVersion()");

            throw;
        }
    }

    /// <summary>
    /// Cleans up expired or orphaned batches from the system.
    /// </summary>
    /// <returns>The cleanup result with removed batches and any errors encountered.</returns>
    [HttpPost("cleanupbatches", Name = nameof(SystemController) + "." + nameof(CleanupBatches))]
    [ProducesResponseType(typeof(CleanupBatchesResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CleanupBatches()
    {
        try
        {
            _logger.LogDebug("System controller CleanupBatches()");

            return Ok(await _systemManager.CleanupBatches());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller CleanupBatches()");

            throw;
        }
    }


    /// <summary>
    /// Cleans up empty datasets from the system.
    /// </summary>
    /// <returns>The cleanup result with removed datasets and any errors encountered.</returns>
    [HttpPost("cleanupdatasets", Name = nameof(SystemController) + "." + nameof(CleanupDatasets))]
    [ProducesResponseType(typeof(CleanupDatasetResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CleanupDatasets()
    {
        try
        {
            _logger.LogDebug("System controller CleanupDatasets()");

            return Ok(await _systemManager.CleanupEmptyDatasets());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller CleanupDatasets()");

            throw;
        }
    }

    /// <summary>
    /// Cleans up build artifacts and stale entries on a single dataset, all datasets in
    /// an organization, or every dataset in the system. Admin only.
    /// When more than one dataset is targeted, the cleanup is enqueued as background jobs.
    /// </summary>
    /// <param name="request">
    /// Selects the cleanup scope. When both slugs are null the cleanup runs across all
    /// organizations. When only OrganizationSlug is set the cleanup runs across all
    /// datasets in that organization. When both are set the cleanup targets a single
    /// dataset and runs synchronously.
    /// </param>
    /// <returns>The cleanup result with removed entries/builds (sync) or job id (async).</returns>
    [HttpPost("cleanup", Name = nameof(SystemController) + "." + nameof(Cleanup))]
    [ProducesResponseType(typeof(CleanupBuildResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(CleanupBuildResultDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Cleanup([FromBody] CleanupBuildRequestDto? request)
    {
        try
        {
            _logger.LogDebug("System controller Cleanup({OrgSlug}/{DsSlug})",
                request?.OrganizationSlug, request?.DatasetSlug);

            var result = await _systemManager.CleanupBuild(request ?? new CleanupBuildRequestDto());
            return result.Async ? Accepted(result) : Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller Cleanup()");

            throw;
        }
    }

    /// <summary>
    /// Migrates dataset visibility settings from the legacy format to the new format.
    /// </summary>
    /// <returns>A list of migrated visibility entries.</returns>
    [HttpPost("migratevisibility", Name = nameof(SystemController) + "." + nameof(MigrateVisibility))]
    [ProducesResponseType(typeof(IEnumerable<MigrateVisibilityEntryDTO>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> MigrateVisibility()
    {
        try
        {
            _logger.LogDebug("System controller MigrateVisibility()");

            return Ok(await _systemManager.MigrateVisibility());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller CleanupDatasets()");

            throw;
        }
    }

    /// <summary>
    /// Gets the status of the build pending background job.
    /// </summary>
    /// <returns>The current status of the build pending job including metrics and next scheduled run.</returns>
    [HttpGet("build-pending-status", Name = nameof(SystemController) + "." + nameof(GetBuildPendingStatus))]
    [ProducesResponseType(typeof(BuildPendingStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetBuildPendingStatus()
    {
        try
        {
            _logger.LogDebug("System controller GetBuildPendingStatus()");

            return Ok(await _systemManager.GetBuildPendingStatus());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller GetBuildPendingStatus()");

            throw;
        }
    }

    /// <summary>
    /// Imports a dataset from another Registry instance.
    /// </summary>
    /// <param name="request">The import request containing source and destination information.</param>
    /// <returns>The import result with imported items, errors, and statistics.</returns>
    [HttpPost("import-dataset", Name = nameof(SystemController) + "." + nameof(ImportDataset))]
    [ProducesResponseType(typeof(ImportResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ImportDataset([FromBody] ImportDatasetRequestDto request)
    {
        try
        {
            _logger.LogDebug("System controller ImportDataset('{SourceOrg}/{SourceDs}' from '{SourceUrl}')",
                request.SourceOrganization, request.SourceDataset, request.SourceRegistryUrl);

            return Ok(await _systemManager.ImportDataset(request));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller ImportDataset('{SourceOrg}/{SourceDs}' from '{SourceUrl}')",
                request?.SourceOrganization, request?.SourceDataset, request?.SourceRegistryUrl);

            throw;
        }
    }

    /// <summary>
    /// Imports an entire organization with all its datasets from another Registry instance.
    /// </summary>
    /// <param name="request">The import request containing source and destination organization information.</param>
    /// <returns>The import result with imported items, errors, and statistics.</returns>
    [HttpPost("import-organization", Name = nameof(SystemController) + "." + nameof(ImportOrganization))]
    [ProducesResponseType(typeof(ImportResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ImportOrganization([FromBody] ImportOrganizationRequestDto request)
    {
        try
        {
            _logger.LogDebug("System controller ImportOrganization('{SourceOrg}' from '{SourceUrl}')",
                request.SourceOrganization, request.SourceRegistryUrl);

            return Ok(await _systemManager.ImportOrganization(request));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller ImportOrganization('{SourceOrg}' from '{SourceUrl}')",
                request?.SourceOrganization, request?.SourceRegistryUrl);

            throw;
        }
    }

    /// <summary>
    /// Moves one or more datasets from one organization to another.
    /// Only administrators can perform this operation.
    /// </summary>
    /// <param name="request">The move request containing the source organization slug, dataset slugs, destination organization, and conflict resolution strategy.</param>
    /// <returns>Results of the move operation for each dataset.</returns>
    [HttpPost("move-datasets", Name = nameof(SystemController) + "." + nameof(MoveDatasets))]
    [ProducesResponseType(typeof(IEnumerable<MoveDatasetResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> MoveDatasets(
        [FromBody, Required] MoveDatasetDto request)
    {
        try
        {
            _logger.LogDebug("System controller MoveDatasets('{SourceOrgSlug}', datasets: [{DatasetSlugs}], destination: '{DestOrgSlug}', conflictResolution: {ConflictResolution})",
                request?.SourceOrgSlug,
                string.Join(", ", request?.DatasetSlugs ?? []),
                request?.DestinationOrgSlug,
                request?.ConflictResolution);

            var results = await _datasetsManager.MoveToOrganization(
                request?.SourceOrgSlug,
                request?.DatasetSlugs,
                request?.DestinationOrgSlug,
                request!.ConflictResolution);

            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller MoveDatasets");
            throw;
        }
    }

    /// <summary>
    /// Merges a source organization into a destination organization.
    /// All datasets from the source will be moved to the destination.
    /// Only administrators can perform this operation.
    /// </summary>
    /// <param name="request">The merge request containing destination organization and options.</param>
    /// <returns>Result of the merge operation.</returns>
    [HttpPost("merge-organizations", Name = nameof(SystemController) + "." + nameof(MergeOrganizations))]
    [ProducesResponseType(typeof(MergeOrganizationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> MergeOrganizations(
        [FromBody, Required] MergeOrganizationDto request)
    {
        try
        {
            _logger.LogDebug("System controller MergeOrganizations('{SourceOrgSlug}' -> '{DestOrgSlug}', conflictResolution: {ConflictResolution}, deleteSource: {DeleteSource})",
                request?.SourceOrgSlug,
                request?.DestinationOrgSlug,
                request?.ConflictResolution,
                request?.DeleteSourceOrganization);

            var result = await _organizationsManager.Merge(
                request?.SourceOrgSlug,
                request?.DestinationOrgSlug,
                request!.ConflictResolution,
                request!.DeleteSourceOrganization);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller MergeOrganizations");
            throw;
        }
    }

    /// <summary>
    /// Removes old terminal (Succeeded/Failed/Deleted) records from the JobIndices table.
    /// Useful for manual cleanup when the table has grown too large.
    /// </summary>
    /// <param name="retentionDays">Optional override for retention period in days. Uses the configured default when omitted.</param>
    /// <returns>Cleanup result with the number of records deleted.</returns>
    [HttpPost("cleanup-jobindices", Name = nameof(SystemController) + "." + nameof(CleanupJobIndices))]
    [ProducesResponseType(typeof(CleanupJobIndicesResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CleanupJobIndices([FromQuery] int? retentionDays = null)
    {
        try
        {
            _logger.LogDebug("System controller CleanupJobIndices(retentionDays: {RetentionDays})", retentionDays);

            return Ok(await _systemManager.CleanupJobIndices(retentionDays));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller CleanupJobIndices()");

            throw;
        }
    }

    /// <summary>
    /// Triggers an immediate, one-off run of the artifact completeness checker.
    /// Scans every entry in every dataset and enqueues a rebuild for any
    /// buildable entry whose build output is missing or empty.
    /// </summary>
    /// <returns>The Hangfire job id of the enqueued scan.</returns>
    [HttpPost("check-artifact-completeness",
        Name = nameof(SystemController) + "." + nameof(CheckArtifactCompleteness))]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public IActionResult CheckArtifactCompleteness()
    {
        try
        {
            _logger.LogDebug("System controller CheckArtifactCompleteness()");

            var jobId = BackgroundJob.Enqueue<ArtifactCompletenessCheckerService>(
                s => s.CheckAndQueueAsync(null));

            return Accepted(new { JobId = jobId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller CheckArtifactCompleteness()");

            throw;
        }
    }

    /// <summary>
    /// Gets the status of all platform feature flags.
    /// </summary>
    /// <returns>An object with the status of each feature.</returns>
    [AllowAnonymous]
    [HttpGet("features", Name = nameof(SystemController) + "." + nameof(GetFeatures))]
    [ProducesResponseType(typeof(FeaturesDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFeatures()
    {
        // Compute the per-tool gating state for the current caller. The features
        // endpoint has no organization context, so the org allowlist is skipped
        // (orgSlug: null) and applied later on the org-scoped tools endpoint.
        var toolStates = await Task.WhenAll(
            _toolRegistry.All.Select(t => _toolGating.EvaluateAsync(t.Id, orgSlug: null)));

        var features = new FeaturesDto
        {
            OrganizationMemberManagement = _appSettings.EnableOrganizationMemberManagement,
            // Derived from the active provider's capability so that LDAP, Remote,
            // and any future external provider all produce false automatically.
            UserManagement = _loginManager.Capabilities.SupportsLocalUserManagement,
            StorageLimiter = _appSettings.EnableStorageLimiter,
            MaxConcurrentDownloadsPerUser = _appSettings.MaxConcurrentDownloadsPerUser,
            DisableAnonymousBulkDownloads = _appSettings.DisableAnonymousBulkDownloads,
            PasswordPolicy = _appSettings.PasswordPolicy != null
                ? new PasswordPolicyDto
                {
                    MinLength = _appSettings.PasswordPolicy.MinLength,
                    RequireDigit = _appSettings.PasswordPolicy.RequireDigit,
                    RequireUppercase = _appSettings.PasswordPolicy.RequireUppercase,
                    RequireLowercase = _appSettings.PasswordPolicy.RequireLowercase,
                    RequireNonAlphanumeric = _appSettings.PasswordPolicy.RequireNonAlphanumeric
                }
                : null,
            DatasetThumbnailCandidates = _appSettings.DatasetThumbnailCandidates,
            MaxExportSizeBytes = _appSettings.MaxExportSizeBytes,
            BulkDownloadAsyncThresholdBytes =
                (_appSettings.ProcessingPlatform ?? new ProcessingPlatformSettings()).BulkDownloadAsyncThresholdBytes,
            TaskTools = _toolRegistry.All
                .Zip(toolStates, (t, st) => new TaskToolInfoDto(t.Id, t.Version, t.Title,
                    t.RequiredAccess.ToString(), t.ProducesArtifact, t.ResultExtension,
                    st.Hidden, st.Disabled, st.DisabledMessage))
                .ToArray(),
            TaskStates = TaskStateCatalog.All
                .Select(s => new TaskStateInfoDto(s, TaskStateCatalog.IsTerminal(s)))
                .ToArray(),
            HubOptions = _appSettings.HubOptions,
            HubVersion = HubInfo.CurrentVersion,
            RegistryVersion = _systemManager.GetVersion(),
            DdbVersion = GetCleanDdbVersion()
        };

        return Ok(features);
    }

    private string GetCleanDdbVersion()
    {
        try
        {
            var raw = _ddbWrapper.GetVersion();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            // The native lib returns "<semver> <commit>" - keep only the semver.
            return raw.Contains(' ') ? raw[..raw.IndexOf(' ')] : raw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve DDB version from native wrapper");
            return null;
        }
    }

    /// <summary>
    /// Generates a global report of all organizations and datasets. Admin only.
    /// </summary>
    /// <returns>Global report containing user, organizations, datasets, and file statistics.</returns>
    [HttpGet("report", Name = nameof(SystemController) + "." + nameof(GetGlobalReport))]
    [ProducesResponseType(typeof(GlobalReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetGlobalReport()
    {
        try
        {
            _logger.LogDebug("System controller GetGlobalReport()");

            return Ok(await _systemManager.GetGlobalReport());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller GetGlobalReport()");

            throw;
        }
    }

    /// <summary>
    /// Gets all Registry configuration fields grouped by section, with default values,
    /// descriptions, and typed metadata for the admin configuration editor page.
    /// Sensitive values (secrets, passwords, tokens) are never exposed - only IsSet flags.
    /// </summary>
    /// <returns>ConfigurationDataDto with all sections and fields.</returns>
    [HttpGet("config", Name = nameof(SystemController) + "." + nameof(GetConfig))]
    [ProducesResponseType(typeof(ConfigurationDataDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    public IActionResult GetConfig()
    {
        try
        {
            _logger.LogDebug("System controller GetConfig()");

            return Ok(_configurationBuilder.Build());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller GetConfig()");

            throw;
        }
    }

    /// <summary>
    /// Gets the list of configured processing nodes (NodeODX) available for photogrammetry tasks.
    /// Only exposes non-sensitive fields (id and title). URL and token are never returned.
    /// </summary>
    /// <returns>A list of processing node descriptors.</returns>
    [HttpGet("processingNodes", Name = nameof(SystemController) + "." + nameof(GetProcessingNodes))]
    [ProducesResponseType(typeof(IEnumerable<ProcessingNodeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public IActionResult GetProcessingNodes()
    {
        try
        {
            _logger.LogDebug("System controller GetProcessingNodes()");

            var nodes = (_appSettings.ProcessingPlatform?.NodeOdx ?? [])
                .Select(n => new ProcessingNodeDto(n.Id, n.Title ?? n.Id))
                .ToArray();

            return Ok(nodes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller GetProcessingNodes()");

            throw;
        }
    }

    /// <summary>
    /// Gets the available processing options from a specific NodeODX processing node.
    /// The options are fetched live from the node and include name, type, domain, help text,
    /// and default value for each option.
    /// </summary>
    /// <param name="nodeId">The id of the processing node.</param>
    /// <returns>A list of processing option descriptors.</returns>
    [HttpGet("processingNodes/{nodeId}/options", Name = nameof(SystemController) + "." + nameof(GetProcessingNodeOptions))]
    [ProducesResponseType(typeof(IEnumerable<NodeOdxOptionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProcessingNodeOptions([Required] string nodeId)
    {
        try
        {
            _logger.LogDebug("System controller GetProcessingNodeOptions({NodeId})", nodeId);

            var nodes = _appSettings.ProcessingPlatform?.NodeOdx ?? [];
            var nodeConfig = nodes.FirstOrDefault(n => n.Id.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
            if (nodeConfig is null)
                return NotFound(new ErrorResponse($"Processing node '{nodeId}' not found."));

            var node = new NodeOdxEndpoint(nodeConfig.Id, nodeConfig.Url, nodeConfig.Token, nodeConfig.Title);

            var options = await _nodeOdxClient.GetOptionsAsync(node, HttpContext.RequestAborted);

            var dtos = options.Select(o => new NodeOdxOptionDto(
                o.Name, o.Type, o.Domain, o.Help, o.Value)).ToArray();

            return Ok(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller GetProcessingNodeOptions({NodeId})", nodeId);
            throw;
        }
    }

    /// <summary>
    /// Checks whether a specific NodeODX processing node is reachable and reports its
    /// queue capacity (used by the "Check Node" action before launching photogrammetry).
    /// Never throws for an unreachable node: returns <c>Reachable=false</c> with the
    /// failure reason so the UI can display it.
    /// </summary>
    /// <param name="nodeId">The id of the processing node.</param>
    /// <returns>The node status descriptor.</returns>
    [HttpGet("processingNodes/{nodeId}/status", Name = nameof(SystemController) + "." + nameof(GetProcessingNodeStatus))]
    [ProducesResponseType(typeof(ProcessingNodeStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProcessingNodeStatus([Required] string nodeId)
    {
        try
        {
            _logger.LogDebug("System controller GetProcessingNodeStatus({NodeId})", nodeId);

            var nodes = _appSettings.ProcessingPlatform?.NodeOdx ?? [];
            var nodeConfig = nodes.FirstOrDefault(n => n.Id.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
            if (nodeConfig is null)
                return NotFound(new ErrorResponse($"Processing node '{nodeId}' not found."));

            var node = new NodeOdxEndpoint(nodeConfig.Id, nodeConfig.Url, nodeConfig.Token, nodeConfig.Title);

            try
            {
                var info = await _nodeOdxClient.GetInfoAsync(node, HttpContext.RequestAborted);
                return Ok(new ProcessingNodeStatusDto(
                    nodeConfig.Id, Reachable: true, info.Version, info.Engine, info.EngineVersion,
                    info.TaskQueueCount, info.MaxParallelTasks, ErrorMessage: null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "NodeODX node '{NodeId}' is unreachable", nodeId);
                return Ok(new ProcessingNodeStatusDto(
                    nodeConfig.Id, Reachable: false, null, null, null, 0, 0, ErrorMessage: ex.Message));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in System controller GetProcessingNodeStatus({NodeId})", nodeId);
            throw;
        }
    }

}