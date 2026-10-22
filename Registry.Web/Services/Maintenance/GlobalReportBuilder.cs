using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Maintenance;

/// <summary>The report of every organization and dataset: sizes and contents by extension.</summary>
public interface IGlobalReportBuilder
{
    /// <summary>The report of all organizations and datasets (a dataset that cannot be read carries its error).</summary>
    Task<GlobalReportDto> BuildAsync(CancellationToken ct = default);
}

/// <summary>Default <see cref="IGlobalReportBuilder"/> (RQ-12, out of <c>SystemManager</c>).</summary>
public sealed class GlobalReportBuilder : IGlobalReportBuilder
{
    private readonly RegistryContext _context;
    private readonly IDdbManager _ddbManager;
    private readonly IAuthManager _authManager;
    private readonly ILogger<GlobalReportBuilder> _logger;

    public GlobalReportBuilder(RegistryContext context, IDdbManager ddbManager, IAuthManager authManager,
        ILogger<GlobalReportBuilder> logger)
    {
        _context = context;
        _ddbManager = ddbManager;
        _authManager = authManager;
        _logger = logger;
    }

    public async Task<GlobalReportDto> BuildAsync(CancellationToken ct = default)
    {
        var userName = await _authManager.SafeGetCurrentUserName();

        var organizations = await _context.Organizations
            .Include(o => o.Datasets)
            .ToListAsync(ct);

        var report = new GlobalReportDto
        {
            UserName = userName,
            Organizations = []
        };

        foreach (var org in organizations)
        {
            var orgDto = new GlobalReportOrganizationDto
            {
                Name = org.Name,
                Slug = org.Slug,
                Datasets = []
            };

            foreach (var ds in org.Datasets)
            {
                try
                {
                    var ddb = _ddbManager.Get(org.Slug, ds.InternalRef);
                    var info = ddb.GetInfo();

                    // Group entries by extension
                    var entries = ddb.FindSummaries(EntryQuery.All);
                    var contents = new List<GlobalReportContentDto>();

                    if (entries.Count != 0)
                    {
                        var grouped = entries
                            .GroupBy(e => string.IsNullOrEmpty(e.Path) ? "" : Path.GetExtension(e.Path).TrimStart('.'))
                            .Select(g => new GlobalReportContentDto
                            {
                                Extension = g.Key,
                                Files = g.Count(),
                                Size = g.Sum(e => e.Size)
                            })
                            .OrderByDescending(c => c.Files)
                            .ToList();

                        contents.AddRange(grouped);
                    }

                    var dsDto = new GlobalReportDatasetDto
                    {
                        Name = ds.Slug,
                        Slug = ds.Slug,
                        CreationDate = ds.CreationDate,
                        Description = org.Description,
                        IsPublic = org.IsPublic,
                        Owner = org.OwnerId,
                        Size = info.Size,
                        Contents = contents
                    };

                    orgDto.Datasets.Add(dsDto);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing dataset {OrgSlug}/{DsSlug}", org.Slug, ds.Slug);
                    // Add dataset with error instead of skipping
                    orgDto.Datasets.Add(new GlobalReportDatasetDto
                    {
                        Name = ds.Slug,
                        Slug = ds.Slug,
                        CreationDate = ds.CreationDate,
                        Description = org.Description,
                        IsPublic = org.IsPublic,
                        Owner = org.OwnerId,
                        Size = 0,
                        Error = ex.Message
                    });
                }
            }

            report.Organizations.Add(orgDto);
        }

        return report;
    }
}
