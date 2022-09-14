using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB.Models;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

public class StacManager : IStacManager
{
    private readonly IAuthManager _authManager;
    private readonly RegistryContext _context;
    private readonly IUtils _utils;
    private readonly IDatasetsManager _datasetManager;
    private readonly ApplicationDbContext _appContext;
    private readonly IDdbManager _ddbManager;
    private readonly ILogger<StacManager> _logger;

    public StacManager(
        IAuthManager authManager,
        RegistryContext context,
        IUtils utils,
        IDatasetsManager datasetManager,
        ApplicationDbContext appContext,
        IDdbManager ddbManager,
        ILogger<StacManager> logger)
    {
        _authManager = authManager;
        _context = context;
        _utils = utils;
        _datasetManager = datasetManager;
        _appContext = appContext;
        _ddbManager = ddbManager;
        _logger = logger;
    }
    
    public async Task<StacCatalogDto> GetCatalog()
    {
        var currentUser = await _authManager.GetCurrentUser();

        if (currentUser == null)
            throw new UnauthorizedException("Invalid user");

        var query = (from dataset in _context.Datasets.Include("Organization").ToArray()
            let ddb = _ddbManager.Get(dataset.Organization.Slug, dataset.InternalRef)
            let visibility = ddb.Meta.GetSafe().Visibility
            where visibility == Visibility.Public
            select new { ds = dataset.Slug, org = dataset.Organization.Slug }).ToArray();
        
        

        /*
        var query = 
            from org in _context.Organizations
            where org.OwnerId == currentUser.Id || org.Slug == MagicStrings.PublicOrganizationSlug
            select org;
            
        // This can be optimized, but it's not a big deal because it's a cross database query anyway
        var usersMapper = await _appContext.Users.Select(item => new { item.Id, item.UserName })
            .ToDictionaryAsync(item => item.Id, item => item.UserName);

        return from org in query
            let userName = org.OwnerId != null ? usersMapper.SafeGetValue(org.OwnerId) : null
            select new OrganizationDto
            {
                CreationDate = org.CreationDate,
                Description = org.Description,
                Slug = org.Slug,
                Name = org.Name,
                Owner = userName,
                IsPublic = org.IsPublic
            };
            
            */

        return null;
    }
}