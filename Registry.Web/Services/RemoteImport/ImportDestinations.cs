#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Services.Builds;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.RemoteImport;

/// <summary>The local dataset a remote dataset is imported into, and its folder.</summary>
public sealed record ImportDestination(string OrganizationSlug, string DatasetSlug, Guid InternalRef, string Folder);

/// <summary>The local side of a remote import: the destination dataset, its folder, its index and its build.</summary>
public sealed class ImportDestinations
{
    private readonly RegistryContext _context;
    private readonly IDdbManager _ddbManager;
    private readonly IFileSystem _fileSystem;
    private readonly BuildScheduler _builds;
    private readonly TimeProvider _time;
    private readonly ILogger<ImportDestinations> _logger;

    public ImportDestinations(RegistryContext context, IDdbManager ddbManager, IFileSystem fileSystem,
        BuildScheduler builds, TimeProvider time, ILogger<ImportDestinations> logger)
    {
        _context = context;
        _ddbManager = ddbManager;
        _fileSystem = fileSystem;
        _builds = builds;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// The destination organization and dataset of <paramref name="source"/>, created when missing (a new
    /// organization is private, described as imported from the source organization).
    /// </summary>
    public async Task<ImportDestination> GetOrCreateAsync(RemoteDataset source, CancellationToken ct)
    {
        var orgSlug = source.DestinationOrganization;
        var dsSlug = source.DestinationDataset;
        var now = _time.GetUtcNow().UtcDateTime;

        var org = await _context.Organizations.FirstOrDefaultAsync(o => o.Slug == orgSlug, ct);
        if (org == null)
        {
            _logger.LogInformation("Creating organization {OrgSlug}", orgSlug);
            org = new Organization
            {
                Slug = orgSlug,
                Name = orgSlug,
                Description = $"Imported from {source.SourceOrganization}",
                CreationDate = now,
                IsPublic = false
            };
            _context.Organizations.Add(org);
            await _context.SaveChangesAsync(ct);
        }

        var dataset = await _context.Datasets.FirstOrDefaultAsync(
            d => d.Slug == dsSlug && d.Organization.Slug == orgSlug, ct);
        if (dataset == null)
        {
            _logger.LogInformation("Creating dataset {OrgSlug}/{DsSlug}", orgSlug, dsSlug);
            dataset = new Dataset
            {
                Slug = dsSlug,
                InternalRef = Guid.NewGuid(),
                CreationDate = now,
                Organization = org
            };
            _context.Datasets.Add(dataset);
            await _context.SaveChangesAsync(ct);
        }

        return new ImportDestination(orgSlug, dsSlug, dataset.InternalRef,
            _ddbManager.GetDatasetFolder(orgSlug, dataset.InternalRef));
    }

    /// <summary>Removes everything in the destination folder, the index included, creating the folder if missing.</summary>
    public void Empty(ImportDestination destination)
    {
        var folder = destination.Folder;
        _fileSystem.FolderCreate(folder);

        var directories = _fileSystem.GetDirectories(folder);
        var files = _fileSystem.GetFiles(folder);
        if (directories.Length == 0 && files.Length == 0)
            return;

        _logger.LogInformation("Removing the content of {Folder} ({DirCount} directories, {FileCount} files)",
            folder, directories.Length, files.Length);
        _ddbManager.ReleaseConnections(folder);
        foreach (var directory in directories)
            _fileSystem.FolderDelete(directory);
        foreach (var file in files)
            _fileSystem.Delete(file);
    }

    /// <summary>The index of the destination (initialized when the folder has none).</summary>
    public IDDB Open(ImportDestination destination) =>
        _ddbManager.Get(destination.OrganizationSlug, destination.InternalRef);

    /// <summary>Queues the forced build of the whole destination dataset.</summary>
    public async Task<string> QueueBuildAsync(ImportDestination destination, IDDB ddb)
    {
        var jobId = await _builds.QueueDatasetAsync(new BuildTarget(ddb,
                new DatasetKey(destination.OrganizationSlug, destination.InternalRef), destination.OrganizationSlug,
                destination.DatasetSlug), force: true);
        _logger.LogInformation("Build of the imported dataset {OrgSlug}/{DsSlug} queued as job {JobId}",
            destination.OrganizationSlug, destination.DatasetSlug, jobId);
        return jobId;
    }
}
