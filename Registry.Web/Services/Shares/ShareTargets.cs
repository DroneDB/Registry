using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Entry = Registry.Web.Data.Models.Entry;

namespace Registry.Web.Services.Shares;

/// <summary>The dataset a share goes to: the tagged one (created when missing; its running batches rolled back), a new one in the tagged organization, or a new one in the user's default organization (created when missing).</summary>
public interface IShareTargets
{
    Task<Dataset> ResolveAsync(ShareInitDto parameters, User currentUser);
}

/// <summary>Default <see cref="IShareTargets"/> (RQ-12, out of <c>ShareManager</c>).</summary>
public sealed class ShareTargets : IShareTargets
{
    private readonly RegistryContext _context;
    private readonly IDatasetResolver _datasets;
    private readonly IOrganizationsManager _organizationsManager;
    private readonly IDatasetsManager _datasetsManager;
    private readonly IShareBatchRollback _rollback;
    private readonly INameGenerator _nameGenerator;
    private readonly ILogger<ShareTargets> _logger;

    public ShareTargets(RegistryContext context, IDatasetResolver datasets, IOrganizationsManager organizationsManager, IDatasetsManager datasetsManager, IShareBatchRollback rollback, INameGenerator nameGenerator, ILogger<ShareTargets> logger)
    {
        _context = context;
        _datasets = datasets;
        _organizationsManager = organizationsManager;
        _datasetsManager = datasetsManager;
        _rollback = rollback;
        _nameGenerator = nameGenerator;
        _logger = logger;
    }

    public async Task<Dataset> ResolveAsync(ShareInitDto parameters, User currentUser)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(currentUser);

        Dataset dataset;
        var tag = parameters.Tag.ToTag();

        // If no tag is specified but OrgSlug is, treat it as "create new dataset in that org"
        // This lets callers specify a destination org without requiring a full "org/dataset" tag.
        if (tag == null && !string.IsNullOrWhiteSpace(parameters.OrgSlug))
        {
            if (!parameters.OrgSlug.IsValidSlug())
                throw new BadRequestException($"Organization slug is not valid: '{parameters.OrgSlug}'");

            // Use a synthetic tag with only the org; dataset slug is null so a new one is generated below
            tag = new TagDto(parameters.OrgSlug, null);
        }

        // No organization requested
        if (tag?.OrganizationSlug == null)
        {
            if (tag?.DatasetSlug != null)
                throw new BadRequestException("Cannot specify a dataset without an organization");

            string orgSlug;

            _logger.LogInformation("No organization and dataset specified");

            var nameSlug = currentUser.UserName.ToSlug();

            // We start from the principle that a default user organization begins with the username in slug form
            // If we notice that this strategy is weak we have to add a new field to the db entry
            var userOrganization = await _context.Organizations.FirstOrDefaultAsync(item =>
                item.OwnerId == currentUser.Id && item.Name.StartsWith(nameSlug));

            // Some idiot removed its own organization, maybe we should prevent this, btw nevermind: let's take care of it
            if (userOrganization == null)
            {
                orgSlug = (await _organizationsManager.AddUserDefaultAsync(currentUser.Id, currentUser.UserName)).Slug;

                _logger.LogInformation("No default user organization found, added a new one: '{OrgSlug}'", orgSlug);

                userOrganization = await _context.Organizations.FirstAsync(item => item.Slug == orgSlug);
            }

            orgSlug = userOrganization.Slug;

            _logger.LogInformation("Using default user organization '{OrgSlug}'", orgSlug);

            var dsSlug = await GetUniqueDatasetSlugAsync();

            _logger.LogInformation("Generated unique dataset slug '{DsSlug}'", dsSlug);

            await _datasetsManager.AddNew(orgSlug, new DatasetNewDto
            {
                Slug = dsSlug,
                Name = parameters.DatasetName,
                Visibility = Visibility.Public
            });

            dataset = await GetTrackedDatasetAsync(orgSlug, dsSlug);

            _logger.LogInformation("Created new dataset '{DsSlug}', creating batch", dsSlug);
        }
        else
        {
            // Check if the requested organization exists
            var organization = await _datasets.GetOrganizationAsync(tag.OrganizationSlug, safe: true, withTracking: true);

            if (organization == null)
                throw new BadRequestException($"Cannot find organization '{tag.OrganizationSlug}'");

            _logger.LogInformation("Organization found");

            // If no dataset is specified, we create a new one with a random slug
            if (tag.DatasetSlug == null)
            {
                var dsSlug = await GetUniqueDatasetSlugAsync();

                _logger.LogInformation("Generated unique dataset slug '{DsSlug}'", dsSlug);

                await _datasetsManager.AddNew(tag.OrganizationSlug, new DatasetNewDto
                {
                    Slug = dsSlug,
                    Name = parameters.DatasetName,
                    Visibility = Visibility.Public
                });

                dataset = await GetTrackedDatasetAsync(tag.OrganizationSlug, dsSlug);

                _logger.LogInformation("Dataset created");
            }
            else
            {
                dataset = await GetTrackedDatasetAsync(tag.OrganizationSlug, tag.DatasetSlug, safe: true);

                // Create dataset if not exists
                if (dataset == null)
                {
                    _logger.LogInformation("Dataset '{DatasetSlug}' not found, creating it", tag.DatasetSlug);

                    await _datasetsManager.AddNew(tag.OrganizationSlug, new DatasetNewDto
                    {
                        Slug = tag.DatasetSlug,
                        Name = parameters.DatasetName,
                        Visibility = Visibility.Public
                    });

                    _logger.LogInformation("Dataset created");

                    dataset = await GetTrackedDatasetAsync(tag.OrganizationSlug, tag.DatasetSlug);
                }
                else
                {
                    // A new batch replaces the running ones (who may write to the dataset only)
                    await _rollback.RollbackRunningAsync(dataset);
                }
            }
        }

        return dataset;
    }

    // Datasets are identified by organization and slug: the slug alone may match another organization's
    private Task<Dataset> GetTrackedDatasetAsync(string orgSlug, string dsSlug, bool safe = false) =>
        _datasets.GetDatasetAsync(orgSlug, dsSlug, safe, withTracking: true);

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
