using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Web.Data;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Shares;

/// <summary>
/// The dataset a share creates (<see cref="ShareTargets"/>): with the slug the share's tag names, else with a slug made
/// from its dataset name (§7.1: <see cref="SlugSuffix"/>), else with a random one; tracked, for the batch that follows.
/// The caller checked the access to the organization.
/// </summary>
public sealed class ShareDatasetCreation
{
    private readonly RegistryContext _context;
    private readonly IDatasetResolver _datasets;
    private readonly IDatasetsManager _datasetsManager;
    private readonly INameGenerator _nameGenerator;
    private readonly ILogger<ShareDatasetCreation> _logger;

    public ShareDatasetCreation(RegistryContext context, IDatasetResolver datasets, IDatasetsManager datasetsManager,
        INameGenerator nameGenerator, ILogger<ShareDatasetCreation> logger)
    {
        _context = context;
        _datasets = datasets;
        _datasetsManager = datasetsManager;
        _nameGenerator = nameGenerator;
        _logger = logger;
    }

    /// <summary>Creates the dataset <paramref name="dsSlug"/> in <paramref name="orgSlug"/>.</summary>
    public async Task<Dataset> CreateAsync(string orgSlug, string dsSlug, string? name, Visibility visibility)
    {
        await _datasetsManager.AddNew(orgSlug, new DatasetNewDto { Slug = dsSlug, Name = name, Visibility = visibility });
        return await GetTrackedDatasetAsync(orgSlug, dsSlug);
    }

    /// <summary>
    /// Creates a dataset in <paramref name="orgSlug"/> with a slug that <paramref name="name"/> gives (§7.1), or a random
    /// one without a name. The check for a free slug is not enough: a request that creates the same slug in between is
    /// answered by the unique index, and this one goes on with the next suffix.
    /// </summary>
    public async Task<Dataset> CreateNewAsync(string orgSlug, string? name, Visibility visibility)
    {
        // The slug comes from the name, which then needs a Latin letter or digit (D-035: the request has no slug field)
        InputRules.EnsureLatinName(name, null, "datasetName", withSlugField: false);

        if (string.IsNullOrWhiteSpace(name))
        {
            var random = await GetUniqueDatasetSlugAsync();
            _logger.LogInformation("Generated unique dataset slug '{DsSlug}'", random);

            await _datasetsManager.AddNew(orgSlug, new DatasetNewDto { Slug = random, Name = name, Visibility = visibility });
            return await GetTrackedDatasetAsync(orgSlug, random);
        }

        var baseSlug = SlugSuffix.FromName(name);
        for (var n = 1; ; n++)
        {
            var slug = SlugSuffix.Candidate(baseSlug, n);
            if (await SlugIsTakenAsync(orgSlug, slug))
                continue;

            try
            {
                await _datasetsManager.AddNew(orgSlug, new DatasetNewDto { Slug = slug, Name = name, Visibility = visibility });
            }
            catch (BadRequestException)
            {
                // Taken since the check: the next one. A refusal with the slug still free is not a lost race
                if (await SlugIsTakenAsync(orgSlug, slug))
                    continue;

                throw;
            }

            _logger.LogInformation("Created dataset '{DsSlug}' from the name '{DatasetName}'", slug, name);
            return await GetTrackedDatasetAsync(orgSlug, slug);
        }
    }

    private Task<bool> SlugIsTakenAsync(string orgSlug, string slug) =>
        _context.Datasets.AsNoTracking().AnyAsync(item => item.Slug == slug && item.Organization.Slug == orgSlug);

    // Datasets are identified by organization and slug: the slug alone may match another organization's
    private Task<Dataset> GetTrackedDatasetAsync(string orgSlug, string dsSlug) =>
        _datasets.GetRequiredDatasetAsync(orgSlug, dsSlug, withTracking: true);

    private async Task<string> GetUniqueDatasetSlugAsync()
    {
        // We are pretty sure this is unique but ALWAYS CHECK
        string dsSlug;
        do
        {
            // NOTE: We could generate a more language friendly slug, like docker does for its running containers
            dsSlug = _nameGenerator.GenerateName().ToSlug();
        } while (await _context.Datasets.AnyAsync(item => item.Slug == dsSlug));

        return dsSlug;
    }
}
