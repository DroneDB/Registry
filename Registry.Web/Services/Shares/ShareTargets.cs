using System;
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
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Shares;

/// <summary>The dataset a share goes to, and whether the request created it (a rollback of the batch then deletes it).</summary>
public sealed record ShareTarget(Dataset Dataset, bool Created);

/// <summary>
/// The dataset a share goes to: the tagged one (created when missing; its running batches rolled back), a new one in the
/// tagged organization, or a new one in the user's default organization (created when missing). A dataset the request
/// creates has the visibility of <see cref="ShareInitDto.Visibility"/> (Public when the request has none: <c>ddb share</c>
/// and older clients, D-009) and, when the request has no dataset slug, a slug made from <see cref="ShareInitDto.DatasetName"/>
/// (§7.1: <see cref="SlugSuffix"/>) or a random one when it has no name.
/// </summary>
public sealed class ShareTargets
{
    private readonly RegistryContext _context;
    private readonly IDatasetResolver _datasets;
    private readonly IOrganizationsManager _organizationsManager;
    private readonly IDatasetsManager _datasetsManager;
    private readonly ShareBatchRollback _rollback;
    private readonly INameGenerator _nameGenerator;
    private readonly IDatasetAccessResolver _access;
    private readonly ILogger<ShareTargets> _logger;

    public ShareTargets(RegistryContext context, IDatasetResolver datasets, IOrganizationsManager organizationsManager, IDatasetsManager datasetsManager, ShareBatchRollback rollback, INameGenerator nameGenerator, IDatasetAccessResolver access, ILogger<ShareTargets> logger)
    {
        _access = access;
        _context = context;
        _datasets = datasets;
        _organizationsManager = organizationsManager;
        _datasetsManager = datasetsManager;
        _rollback = rollback;
        _nameGenerator = nameGenerator;
        _logger = logger;
    }

    /// <summary>
    /// The dataset the share goes to. <paramref name="beforeWrite"/> gets the id of the owner of the organization it goes
    /// to (null: none) once the user's access to write there is checked and before anything is created or rolled back: a
    /// refusal there (the storage quota, FR-XC-26) changes nothing, and an organization the user may not write to is
    /// refused for its access, whatever the state of its owner (SEC-003).
    /// </summary>
    public async Task<ShareTarget> ResolveAsync(ShareInitDto parameters, User currentUser,
        Func<string?, Task>? beforeWrite = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(currentUser);
        beforeWrite ??= _ => Task.CompletedTask;

        Dataset dataset;
        var created = true;
        var visibility = parameters.Visibility ?? Visibility.Public;
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

            _logger.LogInformation("No organization and dataset specified");

            // The default organization of the user is the user's (created for the user when it does not exist yet)
            await beforeWrite(currentUser.Id);

            var nameSlug = currentUser.RequiredUserName().ToSlug();

            // We start from the principle that a default user organization begins with the username in slug form
            // If we notice that this strategy is weak we have to add a new field to the db entry
            var userOrganization = await _context.Organizations.FirstOrDefaultAsync(item =>
                item.OwnerId == currentUser.Id && item.Name.StartsWith(nameSlug));

            // Some idiot removed its own organization, maybe we should prevent this, btw nevermind: let's take care of it
            if (userOrganization == null)
            {
                var addedSlug = (await _organizationsManager.AddUserDefaultAsync(currentUser.Id, currentUser.RequiredUserName())).Slug;

                _logger.LogInformation("No default user organization found, added a new one: '{OrgSlug}'", addedSlug);

                userOrganization = await _context.Organizations.FirstAsync(item => item.Slug == addedSlug);
            }

            var orgSlug = userOrganization.Slug;

            _logger.LogInformation("Using default user organization '{OrgSlug}'", orgSlug);

            dataset = await CreateNewDatasetAsync(orgSlug, parameters.DatasetName, visibility);

            _logger.LogInformation("Created new dataset '{DsSlug}', creating batch", dataset.Slug);
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
                await EnsureMayCreateInAsync(organization, beforeWrite);
                dataset = await CreateNewDatasetAsync(tag.OrganizationSlug, parameters.DatasetName, visibility);

                _logger.LogInformation("Dataset created");
            }
            else
            {
                var existing = await _datasets.GetDatasetAsync(tag.OrganizationSlug, tag.DatasetSlug, safe: true,
                    withTracking: true);

                // Create dataset if not exists
                if (existing == null)
                {
                    await EnsureMayCreateInAsync(organization, beforeWrite);
                    _logger.LogInformation("Dataset '{DatasetSlug}' not found, creating it", tag.DatasetSlug);

                    await _datasetsManager.AddNew(tag.OrganizationSlug, new DatasetNewDto
                    {
                        Slug = tag.DatasetSlug,
                        Name = parameters.DatasetName,
                        Visibility = visibility
                    });

                    _logger.LogInformation("Dataset created");

                    dataset = await GetTrackedDatasetAsync(tag.OrganizationSlug, tag.DatasetSlug);
                }
                else
                {
                    // A new batch replaces the running ones (who may write to the dataset only)
                    await _access.EnsureAccessAsync(existing, AccessType.Write);
                    await beforeWrite(existing.Organization.OwnerId);
                    await _rollback.RollbackRunningAsync(existing);
                    dataset = existing;
                    created = false;
                }
            }
        }

        return new ShareTarget(dataset, created);
    }

    // The check of the creation of a dataset in the organization (write access, as DatasetsManager.AddNew makes it), then
    // beforeWrite with its owner
    private async Task EnsureMayCreateInAsync(Organization organization, Func<string?, Task> beforeWrite)
    {
        await _access.ResolveOrganizationAsync(organization.Slug, AccessType.Write);
        await beforeWrite(organization.OwnerId);
    }

    /// <summary>
    /// Creates a dataset in <paramref name="orgSlug"/> with a slug that <paramref name="name"/> gives (§7.1), or a random
    /// one without a name. The check for a free slug is not enough: a request that creates the same slug in between is
    /// answered by the unique index, and this one goes on with the next suffix.
    /// </summary>
    private async Task<Dataset> CreateNewDatasetAsync(string orgSlug, string? name, Visibility visibility)
    {
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
