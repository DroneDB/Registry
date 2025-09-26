using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json.Linq;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

public class UsersManager : IUsersManager
{
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly IAuthManager _authManager;
    private readonly IOrganizationsManager _organizationsManager;
    private readonly IUtils _utils;
    private readonly ILogger<UsersManager> _logger;
    private readonly ILoginManager _loginManager;
    private readonly UserManager<User> _userManager;
    private readonly AppSettings _appSettings;
    private readonly ApplicationDbContext _applicationDbContext;
    private readonly RegistryContext _registryContext;
    private readonly IConfigurationHelper<AppSettings> _configurationHelper;

    public UsersManager(
        IOptions<AppSettings> appSettings,
        ILoginManager loginManager,
        UserManager<User> userManager,
        RoleManager<IdentityRole> roleManager,
        ApplicationDbContext applicationDbContext,
        IAuthManager authManager,
        IOrganizationsManager organizationsManager,
        IUtils utils,
        ILogger<UsersManager> logger,
        RegistryContext registryContext,
        IConfigurationHelper<AppSettings> configurationHelper)
    {
        _roleManager = roleManager;
        _authManager = authManager;
        _organizationsManager = organizationsManager;
        _utils = utils;
        _logger = logger;
        _registryContext = registryContext;
        _configurationHelper = configurationHelper;
        _applicationDbContext = applicationDbContext;
        _loginManager = loginManager;
        _userManager = userManager;
        _appSettings = appSettings.Value;
    }

    public async Task<AuthenticateResponse> Authenticate(string userName, string password)
    {
        var res = await _loginManager.CheckAccess(userName, password);

        if (!res.Success)
            return null;

        // Create user if not exists because login manager has greenlighed us
        var user = await _userManager.FindByNameAsync(userName) ??
                   await CreateUserInternal(new User { UserName = userName }, password);

        if (!user.Metadata.IsSameAs(res.Metadata))
        {
            user.Metadata = res.Metadata;
            await _applicationDbContext.SaveChangesAsync();
        }

        await SyncRoles(user);

        // authentication successful so generate jwt token
        var tokenDescriptor = await GenerateJwtToken(user);

        return new AuthenticateResponse(user, tokenDescriptor.Token, tokenDescriptor.ExpiresOn);
    }

    public async Task<AuthenticateResponse> Authenticate(string token)
    {
        // Internal auth does not support token auth
        if (_appSettings.ExternalAuthUrl == null)
            return null;

        var res = await _loginManager.CheckAccess(token);

        if (!res.Success)
            return null;

        // Create user if not exists because login manager has greenlighed us
        var user = await _userManager.FindByNameAsync(res.UserName) ??
                   await CreateUserInternal(new User { UserName = res.UserName }, CommonUtils.RandomString(16));

        if (!user.Metadata.IsSameAs(res.Metadata))
        {
            user.Metadata = res.Metadata;
            await _applicationDbContext.SaveChangesAsync();
        }

        await SyncRoles(user);

        // Authentication successful so generate jwt token
        var tokenDescriptor = await GenerateJwtToken(user);

        return new AuthenticateResponse(user, tokenDescriptor.Token, tokenDescriptor.ExpiresOn);
    }

    private async Task SyncRoles(User user)
    {
        var tmp = user.Metadata?.SafeGetValue("roles");
        var roles = tmp as string[] ?? (tmp as JArray)?.ToObject<string[]>();
        if (roles == null)
            return;

        // Remove all pre-existing roles
        var userRoles = await _userManager.GetRolesAsync(user);
        foreach (var role in userRoles)
            await _userManager.RemoveFromRoleAsync(user, role);

        var edited = false;

        // Sync roles
        foreach (var role in roles)
        {
            // Create missing roles
            if (!await _roleManager.RoleExistsAsync(role))
                await _roleManager.CreateAsync(new IdentityRole { Name = role });

            // Skip if already in this role
            if (await _userManager.IsInRoleAsync(user, role)) continue;

            await _userManager.AddToRoleAsync(user, role);
            edited = true;
        }

        if (edited)
        {
            _applicationDbContext.Entry(user).State = EntityState.Modified;
            await _applicationDbContext.SaveChangesAsync();
        }
    }

    public async Task<UserDto> CreateUser(string userName, string email, string password, string[] roles)
    {
        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("Only admins can create new users");

        if (!IsValidUserName(userName))
            throw new ArgumentException("The provided username is not valid");

        var user = await _userManager.FindByNameAsync(userName);

        if (user != null)
            throw new InvalidOperationException("User already exists");

        user = await CreateUserInternal(userName, email, password, roles);

        _logger.LogInformation("User {UserName} created successfully", user.UserName);

        return new UserDto
        {
            UserName = user.UserName,
            Email = user.Email,
            Roles = roles ?? [],
            Organizations = _registryContext.Organizations
                .AsNoTracking()
                .Where(org => org.OwnerId == user.Id || org.Users.Any(item => item.UserId == user.Id))
                .Select(org => org.Slug)
                .ToArray()
        };
    }

    private static bool IsValidUserName(string userName)
    {
        return Regex.IsMatch(userName, "^[a-z0-9]{1,127}$", RegexOptions.Singleline);
    }

    private async Task<User> CreateUserInternal(User user, string password, string[] roles = null)
    {
        await ValidateRoles(roles);

        var res = await _userManager.CreateAsync(user, password);

        if (!res.Succeeded)
        {
            var errors = string.Join(";", res.Errors.Select(item => $"{item.Code} - {item.Description}"));
            _logger.LogWarning("Errors in creating user: {Errors}", errors);

            throw new InvalidOperationException("Error in creating user");
        }

        await CreateUserDefaultOrganization(user);
        await AddUserToRoles(user, roles);

        return user;
    }

    private async Task AddUserToRoles(User user, string[] roles)
    {
        // Add user roles
        if (roles != null && roles.Length != 0)
        {
            foreach (var role in roles)
                await _userManager.AddToRoleAsync(user, role);
        }
    }

    private async Task<User> CreateUserInternal(string userName, string email, string password, string[] roles)
    {
        var user = new User
        {
            UserName = userName,
            Email = email
        };

        return await CreateUserInternal(user, password, roles);
    }

    private async Task ValidateRoles(string[] roles)
    {
        if (roles == null || roles.Length == 0)
            return;

        foreach (var role in roles)
        {
            if (!await _roleManager.RoleExistsAsync(role))
                throw new ArgumentException($"Role {role} does not exist");
        }
    }

    private async Task CreateUserDefaultOrganization(User user)
    {
        var orgSlug = _utils.GetFreeOrganizationSlug(user.UserName);

        await _organizationsManager.AddNew(new OrganizationDto
        {
            Name = user.UserName,
            IsPublic = false,
            CreationDate = DateTime.Now,
            Owner = user.Id,
            Slug = orgSlug
        }, true);
    }

    public async Task<ChangePasswordResult> ChangePassword(string userName, string currentPassword, string newPassword)
    {
        var user = await _userManager.FindByNameAsync(userName);

        if (user == null)
            throw new BadRequestException("User does not exist");

        return await ChangePasswordInternal(user, currentPassword, newPassword);
    }

    public async Task<ChangePasswordResult> ChangePassword(string currentPassword, string newPassword)
    {
        var currentUser = await _authManager.GetCurrentUser();

        if (currentUser == null)
            throw new BadRequestException("User is not authenticated");

        return await ChangePasswordInternal(currentUser, currentPassword, newPassword);
    }

    private async Task<ChangePasswordResult> ChangePasswordInternal(User user, string currentPassword,
        string newPassword)
    {
        IdentityResult res;
        if (currentPassword == null)
        {
            // We need to check if the user is an admin, in this case we proceed with the reset
            if (!await _authManager.IsUserAdmin())
                throw new UnauthorizedException("Current password is required for non-admin users");

            // If the user is an admin, we allow changing password without current password
            _logger.LogInformation("Admin user {UserName} is changing password without current password",
                user.UserName);

            // If the user is an admin, we allow changing password without current password
            if (newPassword == null)
                throw new BadRequestException("New password cannot be null");

            var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
            res = await _userManager.ResetPasswordAsync(user, resetToken, newPassword);

            if (!res.Succeeded)
            {
                var errors = string.Join(";", res.Errors.Select(item => $"{item.Code} - {item.Description}"));
                _logger.LogWarning("Errors in resetting user password: {Errors}", errors);
                throw new InvalidOperationException("Cannot reset password: " + errors);
            }

            _logger.LogInformation("Admin user {UserName} changed password without current password", user.UserName);

            // If this is the default admin password, we should persist it in the config
            if (user.UserName == _appSettings.DefaultAdmin.UserName)
            {
                _appSettings.DefaultAdmin.Password = newPassword;
                _configurationHelper.SaveConfiguration(_appSettings);
            }

            return new ChangePasswordResult
            {
                UserName = user.UserName,
                Password = newPassword
            };
        }

        res = await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);

        if (!res.Succeeded)
        {
            var errors = string.Join(";", res.Errors.Select(item => $"{item.Code} - {item.Description}"));
            _logger.LogWarning("Errors in changing user password: {Errors}", errors);

            throw new InvalidOperationException("Cannot change user password: " + errors);
        }

        // If this is the default admin password, we should persist it in the config
        if (user.UserName == _appSettings.DefaultAdmin.UserName)
        {
            _appSettings.DefaultAdmin.Password = newPassword;
            _configurationHelper.SaveConfiguration(_appSettings);
        }

        _logger.LogInformation("User {UserName} changed password", user.UserName);

        return new ChangePasswordResult
        {
            UserName = user.UserName,
            Password = newPassword
        };
    }

    public async Task<AuthenticateResponse> Refresh()
    {
        var user = await _authManager.GetCurrentUser();

        if (!await _authManager.CanRefreshToken(user))
            throw new UnauthorizedException("User cannot refresh token");

        var tokenDescriptor = await GenerateJwtToken(user);

        return new AuthenticateResponse(user, tokenDescriptor.Token, tokenDescriptor.ExpiresOn);
    }

    public async Task<UserStorageInfo> GetUserStorageInfo(string userName = null)
    {
        var user = await _authManager.GetCurrentUser();

        if (user == null)
            throw new BadRequestException("User does not exist");

        if (string.IsNullOrWhiteSpace(userName))
            return _utils.GetUserStorage(user);

        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("Cannot get other user's storage info");

        user = await _userManager.FindByNameAsync(userName);

        if (user == null)
            throw new BadRequestException("Cannot find user " + userName);

        return _utils.GetUserStorage(user);
    }

    public async Task<Dictionary<string, object>> GetUserMeta(string userName = null)
    {
        var user = await _authManager.GetCurrentUser();

        if (user == null)
            throw new BadRequestException("User does not exist");

        if (string.IsNullOrWhiteSpace(userName))
            return user.Metadata;

        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("Cannot get other user's meta");

        user = await _userManager.FindByNameAsync(userName);

        if (user == null)
            throw new BadRequestException("Cannot find user " + userName);

        return user.Metadata;
    }

    public async Task SetUserMeta(string userName, Dictionary<string, object> meta)
    {
        var user = await _authManager.GetCurrentUser();

        if (user == null)
            throw new BadRequestException("User does not exist");

        if (string.IsNullOrWhiteSpace(userName))
            throw new BadRequestException("User name cannot be empty");

        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("Cannot change user data");

        user = await _userManager.FindByNameAsync(userName);

        if (user == null)
            throw new BadRequestException("Cannot find user " + userName);

        user.Metadata = meta;

        await SyncRoles(user);

        await _applicationDbContext.SaveChangesAsync();
    }

    public async Task<string[]> GetRoles()
    {
        var roles = _roleManager.Roles.Select(role => role.Name);

        return await roles.ToArrayAsync();
    }

    public async Task UpdateUser(string userName, string email)
    {
        if (string.IsNullOrWhiteSpace(userName))
            throw new BadRequestException("User name cannot be empty");

        // If the user is not an admin, they can only update their own data
        if (string.IsNullOrWhiteSpace(email))
            throw new BadRequestException("Email cannot be empty");

        if (!await _authManager.IsUserAdmin())
        {
            // Check if the user is trying to update their own data
            var currentUser = await _authManager.GetCurrentUser();
            if (currentUser == null || currentUser.UserName != userName)
                throw new UnauthorizedException("Only admins can update users");
        }

        var user = await _userManager.FindByNameAsync(userName);

        if (user == null)
            throw new BadRequestException("User does not exist");

        if (!string.IsNullOrWhiteSpace(email))
            user.Email = email;

        _applicationDbContext.Entry(user).State = EntityState.Modified;
        await _applicationDbContext.SaveChangesAsync();

    }

    public async Task<OrganizationDto[]> GetUserOrganizations(string userName)
    {
        var currentUser = await _authManager.GetCurrentUser();

        if (currentUser == null)
            throw new BadRequestException("User does not exist");

        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("Cannot get other user's meta");

        var user = await _userManager.FindByNameAsync(userName);

        if (user == null)
            throw new BadRequestException("Cannot find user " + userName);

        // Use projection to load only necessary data for DTOs
        var orgDtos = await _registryContext.Organizations
            .AsNoTracking()
            .Where(org => org.OwnerId == user.Id || org.Users.Any(item => item.UserId == user.Id))
            .Select(org => new OrganizationDto
            {
                CreationDate = org.CreationDate,
                Description = org.Description,
                Slug = org.Slug,
                Name = org.Name,
                IsPublic = org.IsPublic,
                Owner = org.OwnerId
            })
            .ToArrayAsync();

        return orgDtos;
    }

    public async Task SetUserOrganizations(string userName, string[] orgSlugs)
    {
        var currentUser = await _authManager.GetCurrentUser();

        if (currentUser == null)
            throw new BadRequestException("User does not exist");

        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("Cannot get other user's meta");

        var user = await _userManager.FindByNameAsync(userName);

        if (user == null)
            throw new BadRequestException("Cannot find user " + userName);

        var orgs = (from org in _registryContext.Organizations.Include(o => o.Users).AsNoTracking()
            where org.OwnerId == user.Id || org.Users.Any(item => item.UserId == user.Id)
            select org).ToArray();

        _logger.LogInformation("User {UserName} is in {Count} organizations: {Orgs}", userName, orgs.Length,
            orgs.Select(o => o.Slug).ToArray());

        var orgsDict = orgs.ToDictionary(item => item.Slug, item => item);

        // Remove duplicates
        orgSlugs = orgSlugs.Distinct().ToArray();

        // Find out what orgs to add
        var toAdd = orgSlugs.Where(slug => !orgsDict.ContainsKey(slug)).ToArray();

        _logger.LogInformation("User {UserName} will be added to {Count} organizations: {Orgs}", userName, toAdd.Length,
            toAdd);

        // Add user to orgs
        foreach (var slug in toAdd)
        {
            var org = await _registryContext.Organizations.Include(o => o.Users)
                .FirstOrDefaultAsync(item => item.Slug == slug);

            if (org == null)
                throw new BadRequestException("Organization does not exist: " + slug);

            org.Users.Add(new OrganizationUser
            {
                UserId = user.Id,
                OrganizationSlug = org.Slug
            });
        }

        // Find out what orgs to remove (except the owner)
        var toRemove = orgs.Where(org => !orgSlugs.Contains(org.Slug) && org.OwnerId != user.Id).ToArray();

        _logger.LogInformation("User {UserName} will be removed from {Count} organizations: {Orgs}", userName,
            toRemove.Length, toRemove.Select(o => o.Slug).ToArray());

        // Remove user from orgs
        foreach (var org in toRemove)
        {
            var orgUser = await _registryContext.OrganizationsUsers.FirstOrDefaultAsync(item =>
                item.OrganizationSlug == org.Slug && item.UserId == user.Id);

            if (orgUser == null)
                throw new BadRequestException("User is not in organization: " + org.Slug);

            _registryContext.OrganizationsUsers.Remove(orgUser);
        }

        await _registryContext.SaveChangesAsync();
    }

    public async Task DeleteUser(string userName)
    {
        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("Only admins can delete users");

        if (string.IsNullOrWhiteSpace(userName))
            throw new UnauthorizedException("userName should not be empty");

        if (userName == MagicStrings.AnonymousUserName)
            throw new UnauthorizedException("Cannot delete the anonymous user");

        if (userName == _appSettings.DefaultAdmin.UserName)
            throw new UnauthorizedException("Cannot delete the default admin");

        var user = await _userManager.FindByNameAsync(userName);

        if (user == null)
            throw new BadRequestException("User does not exist");

        var res = await _userManager.DeleteAsync(user);

        if (!res.Succeeded)
        {
            var errors = string.Join(";", res.Errors.Select(item => $"{item.Code} - {item.Description}"));
            _logger.LogWarning("Errors in deleting user: {Errors}", errors);

            throw new InvalidOperationException("Cannot delete user: " + errors);
        }
    }

    public async Task<IEnumerable<UserDto>> GetAll()
    {
        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("User is not admin");

        var userOrgQuery = from orgusr in _registryContext.OrganizationsUsers
            group orgusr by orgusr.UserId
            into g
            select new
            {
                UserId = g.Key,
                OrgIds = g.Select(item => item.OrganizationSlug).ToArray()
            };

        var userOrgQuery2 = from org in _registryContext.Organizations
            where org.OwnerId != null
            group org by org.OwnerId
            into g
            select new
            {
                UserId = g.Key,
                OrgIds = g.Select(item => item.Slug).ToArray()
            };

        var union = userOrgQuery.ToArray().Union(userOrgQuery2.ToArray());

        var merge = from m in union
            group m by m.UserId
            into g
            select new
            {
                UserId = g.Key,
                OrgIds = g.SelectMany(item => item.OrgIds).ToArray()
            };

        var userOrg = merge.ToDictionary(item => item.UserId, item => item.OrgIds);


        var query = (from user in _applicationDbContext.Users
            join userRole in _applicationDbContext.UserRoles on user.Id equals userRole.UserId into userRoles
            from userRole in userRoles.DefaultIfEmpty()
            join role in _applicationDbContext.Roles on userRole.RoleId equals role.Id into roles
            from role in roles.DefaultIfEmpty()
            select new
            {
                UserId = user.Id,
                user.UserName,
                user.Email,
                RoleName = role.Name
            }).ToArray();

        var users = from item in query
            group item by item.UserId
            into grp
            let first = grp.First()
            select new UserDto
            {
                UserName = first.UserName,
                Email = first.Email,
                Roles = grp.Select(item => item.RoleName).ToArray(),
                Organizations = userOrg.SafeGetValue(first.UserId)
            };

        return users;
    }

    public async Task<IEnumerable<UserDetailDto>> GetAllDetailed()
    {
        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("User is not admin");

        var userOrgQuery = from orgusr in _registryContext.OrganizationsUsers
            group orgusr by orgusr.UserId
            into g
            select new
            {
                UserId = g.Key,
                OrgIds = g.Select(item => item.OrganizationSlug).ToArray(),
                OrgCount = g.Count()
            };

        var userOrgQuery2 = from org in _registryContext.Organizations
            where org.OwnerId != null
            group org by org.OwnerId
            into g
            select new
            {
                UserId = g.Key,
                OrgIds = g.Select(item => item.Slug).ToArray(),
                OrgCount = g.Count()
            };

        var union = userOrgQuery.ToArray().Union(userOrgQuery2.ToArray());

        var merge = from m in union
            group m by m.UserId
            into g
            select new
            {
                UserId = g.Key,
                OrgIds = g.SelectMany(item => item.OrgIds).ToArray(),
                OrgCount = g.Sum(item => item.OrgCount)
            };

        var userOrg = merge.ToDictionary(item => item.UserId,
            item => new { OrgIds = item.OrgIds, OrgCount = item.OrgCount });

        // Dataset count per organization
        var datasetQuery = from ds in _registryContext.Datasets
            group ds by ds.Organization.Slug
            into g
            select new
            {
                OrgSlug = g.Key,
                DatasetCount = g.Count()
            };

        var datasetCounts = datasetQuery.ToDictionary(item => item.OrgSlug, item => item.DatasetCount);

        var query = (from user in _applicationDbContext.Users
            join userRole in _applicationDbContext.UserRoles on user.Id equals userRole.UserId into userRoles
            from userRole in userRoles.DefaultIfEmpty()
            join role in _applicationDbContext.Roles on userRole.RoleId equals role.Id into roles
            from role in roles.DefaultIfEmpty()
            select new
            {
                UserId = user.Id,
                user.UserName,
                user.Email,
                RoleName = role.Name
            }).ToArray();

        var users = new List<UserDetailDto>();

        foreach (var userGroup in query.GroupBy(item => item.UserId))
        {
            var first = userGroup.First();
            var orgInfo = userOrg.SafeGetValue(first.UserId);
            var storageInfo = await GetUserStorageInfo(first.UserName);

            // Calculate total datasets for this user across all their organizations
            var totalDatasets = 0;
            if (orgInfo?.OrgIds != null)
            {
                totalDatasets = orgInfo.OrgIds.Sum(orgSlug => datasetCounts.SafeGetValue(orgSlug));
            }

            users.Add(new UserDetailDto
            {
                UserName = first.UserName,
                Email = first.Email,
                Roles = userGroup.Select(item => item.RoleName).Where(r => !string.IsNullOrEmpty(r)).ToArray(),
                Organizations = orgInfo?.OrgIds ?? [],
                StorageQuota = storageInfo.Total,
                StorageUsed = storageInfo.Used,
                OrganizationCount = orgInfo?.OrgCount ?? 0,
                DatasetCount = totalDatasets,
                CreatedDate = DateTime.UtcNow // Placeholder since User doesn't have Created field
            });
        }

        return users;
    }

    public async Task CreateRole(string roleName)
    {
        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("User is not admin");

        if (string.IsNullOrWhiteSpace(roleName))
            throw new ArgumentException("Role name cannot be empty");

        var roleExists = await _roleManager.RoleExistsAsync(roleName);
        if (roleExists)
            throw new ArgumentException($"Role '{roleName}' already exists");

        var result = await _roleManager.CreateAsync(new IdentityRole(roleName));
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Failed to create role: {string.Join(", ", result.Errors.Select(e => e.Description))}");
    }

    public async Task DeleteRole(string roleName)
    {
        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("User is not admin");

        if (string.IsNullOrWhiteSpace(roleName))
            throw new ArgumentException("Role name cannot be empty");

        // Do not allow deletion of the admin role
        if (roleName.Equals(ApplicationDbContext.AdminRoleName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Cannot delete the admin role");

        var role = await _roleManager.FindByNameAsync(roleName);
        if (role == null)
            throw new ArgumentException($"Role '{roleName}' does not exist");

        var result = await _roleManager.DeleteAsync(role);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Failed to delete role: {string.Join(", ", result.Errors.Select(e => e.Description))}");
    }

    public async Task UpdateUserRoles(string userName, string[] roles)
    {
        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("User is not admin");

        var user = await _userManager.FindByNameAsync(userName);
        if (user == null)
            throw new ArgumentException($"User '{userName}' not found");

        // Do not allow removing the admin role from the admin user
        if (userName.Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            if (!roles.Contains(ApplicationDbContext.AdminRoleName, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("Cannot remove admin role from admin user");
        }

        var currentRoles = await _userManager.GetRolesAsync(user);

        // Remove all current roles
        if (currentRoles.Any())
        {
            var removeResult = await _userManager.RemoveFromRolesAsync(user, currentRoles);
            if (!removeResult.Succeeded)
                throw new InvalidOperationException(
                    $"Failed to remove current roles: {string.Join(", ", removeResult.Errors.Select(e => e.Description))}");
        }

        // Add new roles
        if (roles?.Length > 0)
        {
            var addResult = await _userManager.AddToRolesAsync(user, roles);
            if (!addResult.Succeeded)
                throw new InvalidOperationException(
                    $"Failed to add new roles: {string.Join(", ", addResult.Errors.Select(e => e.Description))}");
        }
    }

    private async Task<JwtDescriptor> GenerateJwtToken(User user)
    {
        // Generate token
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.ASCII.GetBytes(_appSettings.Secret);

        var expiresOn = DateTime.UtcNow.AddDays(_appSettings.TokenExpirationInDays);

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([
                new Claim(ClaimTypes.Name, user.Id),
                new Claim(ApplicationDbContext.AdminRoleName.ToLowerInvariant(),
                    (await _userManager.IsInRoleAsync(user, ApplicationDbContext.AdminRoleName)).ToString(),
                    ClaimValueTypes.Boolean)
            ]),
            Expires = expiresOn,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256Signature)
        };
        var token = tokenHandler.CreateToken(tokenDescriptor);

        return new JwtDescriptor
        {
            Token = tokenHandler.WriteToken(token),
            ExpiresOn = expiresOn
        };
    }
}