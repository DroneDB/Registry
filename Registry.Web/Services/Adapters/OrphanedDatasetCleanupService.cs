#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Console;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Data;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Service for cleaning up orphaned dataset folders.
/// Scans the datasets directory for folders that no longer have a corresponding database entry.
/// </summary>
public class OrphanedDatasetCleanupService
{
    private readonly RegistryContext _context;
    private readonly AppSettings _settings;
    private readonly ILogger<OrphanedDatasetCleanupService> _logger;

    public OrphanedDatasetCleanupService(
        RegistryContext context,
        IOptions<AppSettings> settings,
        ILogger<OrphanedDatasetCleanupService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Scans all organization folders and removes dataset folders that don't have a corresponding database entry.
    /// This handles cleanup for datasets that failed to delete properly (e.g., due to locked files).
    /// </summary>
    /// <param name="context">Hangfire context for logging</param>
    [AutomaticRetry(Attempts = 1)]
    public async Task CleanupOrphanedFoldersAsync(PerformContext? context = null)
    {
        void WriteLine(string message)
        {
            _logger.LogInformation(message);
            context?.WriteLine(message);
        }

        var datasetsPath = _settings.DatasetsPath;

        if (string.IsNullOrWhiteSpace(datasetsPath) || !Directory.Exists(datasetsPath))
        {
            WriteLine($"Datasets path '{datasetsPath}' does not exist, skipping orphaned folder cleanup");
            return;
        }

        WriteLine($"Starting orphaned dataset folder cleanup in '{datasetsPath}'");

        var totalOrphaned = 0;
        var totalDeleted = 0;
        var totalFailed = 0;

        // Get all organizations with their datasets from the database
        var orgDatasets = await _context.Datasets
            .AsNoTracking()
            .Include(d => d.Organization)
            .Select(d => new { OrgSlug = d.Organization.Slug, d.InternalRef })
            .ToListAsync();

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
            var validRefs = validFolders.GetValueOrDefault(orgSlug) ?? new HashSet<Guid>();

            // Scan dataset folders within this organization
            foreach (var datasetDir in Directory.EnumerateDirectories(orgDir))
            {
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
                WriteLine($"Found orphaned folder: {datasetDir}");

                try
                {
                    Directory.Delete(datasetDir, recursive: true);
                    totalDeleted++;
                    WriteLine($"Deleted orphaned folder: {datasetDir}");
                }
                catch (Exception ex)
                {
                    totalFailed++;
                    _logger.LogWarning(ex, "Failed to delete orphaned folder: {FolderPath}", datasetDir);
                    context?.WriteLine($"WARNING: Failed to delete orphaned folder {datasetDir}: {ex.Message}");
                }
            }

            // Check if organization folder is now empty and has no datasets in DB
            if (!validFolders.ContainsKey(orgSlug) && IsDirectoryEmpty(orgDir))
            {
                try
                {
                    Directory.Delete(orgDir);
                    WriteLine($"Deleted empty organization folder: {orgDir}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete empty organization folder: {FolderPath}", orgDir);
                }
            }
        }

        WriteLine($"Orphaned folder cleanup completed: {totalOrphaned} found, {totalDeleted} deleted, {totalFailed} failed");
    }

    private static bool IsDirectoryEmpty(string path)
    {
        return !Directory.EnumerateFileSystemEntries(path).Any();
    }
}
