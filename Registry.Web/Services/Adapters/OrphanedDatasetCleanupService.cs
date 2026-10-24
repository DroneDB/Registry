#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Data;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Jobs;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Service for cleaning up orphaned dataset folders.
/// Scans the datasets directory for folders that no longer have a corresponding database entry.
/// </summary>
/// <remarks>
/// One run at a time across the hosts: two would delete the same folders in parallel and report
/// each other's deletions as failures. Timeout 0: the run in progress does the work.
/// </remarks>
[AutomaticRetry(Attempts = 1)]
[DisableConcurrentExecution(timeoutInSeconds: 0)]
public class OrphanedDatasetCleanupService
{
    private readonly RegistryContext _context;
    private readonly StorageOptions _settings;
    private readonly ILogger<OrphanedDatasetCleanupService> _logger;

    public OrphanedDatasetCleanupService(
        RegistryContext context,
        IOptions<StorageOptions> settings,
        ILogger<OrphanedDatasetCleanupService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The job as stored before RQ-09 (D4): runs with the context's shutdown token.</summary>
    public Task CleanupOrphanedFoldersAsync(PerformContext? context) =>
        CleanupOrphanedFoldersAsync(context, JobCancellation.Of(context));

    /// <summary>
    /// Scans all organization folders and removes dataset folders that don't have a corresponding database entry.
    /// This handles cleanup for datasets that failed to delete properly (e.g., due to locked files).
    /// </summary>
    /// <param name="context">Hangfire context for logging</param>
    /// <param name="cancellationToken">Cancelled when the server shuts down: the scan stops before the next folder.</param>
    public async Task CleanupOrphanedFoldersAsync(PerformContext? context, CancellationToken cancellationToken)
    {
        var console = JobConsole.For(_logger, context);

        var datasetsPath = _settings.DatasetsPath;

        if (string.IsNullOrWhiteSpace(datasetsPath) || !Directory.Exists(datasetsPath))
        {
            console.Info("Datasets path '{DatasetsPath}' does not exist, skipping orphaned folder cleanup", datasetsPath);
            return;
        }

        console.Info("Starting orphaned dataset folder cleanup in '{DatasetsPath}'", datasetsPath);

        var totalOrphaned = 0;
        var totalDeleted = 0;
        var totalFailed = 0;

        // Get all organizations with their datasets from the database
        var orgDatasets = await _context.Datasets
            .AsNoTracking()
            .Include(d => d.Organization)
            .Select(d => new { OrgSlug = d.Organization.Slug, d.InternalRef })
            .ToListAsync(cancellationToken);

        // Build a lookup of valid dataset folders (orgSlug -> set of InternalRef GUIDs)
        var validFolders = orgDatasets
            .GroupBy(x => x.OrgSlug)
            .ToDictionary(
                g => g.Key,
                g => new HashSet<Guid>(g.Select(x => x.InternalRef)));

        // Scan each organization folder
        foreach (var orgDir in Directory.EnumerateDirectories(datasetsPath))
        {
            var orgSlug = Path.GetFileName(orgDir);

            // Get valid InternalRefs for this organization
            var validRefs = validFolders.GetValueOrDefault(orgSlug) ?? [];

            // Scan dataset folders within this organization
            foreach (var datasetDir in Directory.EnumerateDirectories(orgDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folderName = Path.GetFileName(datasetDir);

                // Check if folder name is a valid GUID
                if (!Guid.TryParse(folderName, out var internalRef))
                {
                    _logger.LogDebug("Skipping non-GUID folder: {FolderPath}", datasetDir);
                    continue;
                }

                // Check if this folder has a corresponding database entry
                if (validRefs.Contains(internalRef))
                {
                    continue; // Valid folder, skip
                }

                // This is an orphaned folder
                totalOrphaned++;
                console.Info("Found orphaned folder: {Folder}", datasetDir);

                try
                {
                    Directory.Delete(datasetDir, recursive: true);
                    totalDeleted++;
                    console.Info("Deleted orphaned folder: {Folder}", datasetDir);
                }
                catch (Exception ex)
                {
                    totalFailed++;
                    console.Warning(ex, "Failed to delete orphaned folder {Folder}: {Error}", datasetDir, ex.Message);
                }
            }

            // Check if organization folder is now empty and has no datasets in DB
            if (!validFolders.ContainsKey(orgSlug) && IsDirectoryEmpty(orgDir))
            {
                try
                {
                    Directory.Delete(orgDir);
                    console.Info("Deleted empty organization folder: {Folder}", orgDir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete empty organization folder: {FolderPath}", orgDir);
                }
            }
        }

        console.Info("Orphaned folder cleanup completed: {Found} found, {Deleted} deleted, {Failed} failed",
            totalOrphaned, totalDeleted, totalFailed);
    }

    private static bool IsDirectoryEmpty(string path)
    {
        return !Directory.EnumerateFileSystemEntries(path).Any();
    }
}
