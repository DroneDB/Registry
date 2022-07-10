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
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers
{
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
            var user = await _userManager.FindByNameAsync(userName);

            if (user == null)
            {
                user = await CreateUserInternal(new User { UserName = userName }, password);
                await SyncRoles(user);
            }

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

            await SyncRoles(user);

            // authentication successful so generate jwt token
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

        public async Task<User> CreateUser(string userName, string email, string password, string[] roles)
        {
            if (!await _authManager.IsUserAdmin())
                throw new UnauthorizedException("Only admins can create new users");

            if (!IsValidUserName(userName))
                throw new ArgumentException("The provided username is not valid");

            var user = await _userManager.FindByNameAsync(userName);

            if (user != null)
                throw new InvalidOperationException("User already exists");

            return await CreateUserInternal(userName, email, password, roles);
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
            if (roles != null && roles.Any())
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
            if (roles == null || !roles.Any())
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
        
        private async Task<ChangePasswordResult> ChangePasswordInternal(User user, string currentPassword, string newPassword)
        {
            var res = await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);

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

            if (user == null)
                throw new BadRequestException("User does not exist");

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
            await _applicationDbContext.SaveChangesAsync();
        }

        public Task<string[]> GetRoles()
        {
            var roles = _roleManager.Roles.Select(role => role.Name);

            return Task.FromResult(roles.ToArray());
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

            var userOrg = (from orgusr in _registryContext.OrganizationsUsers
                    group orgusr by orgusr.UserId
                    into g
                    select new { UserId = g.Key, OrgIds = g.Select(item => item.OrganizationSlug).ToArray() })
                .ToDictionary(item => item.UserId, item => item.OrgIds);

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

        private async Task<JwtDescriptor> GenerateJwtToken(User user)
        {
            // generate token
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.ASCII.GetBytes(_appSettings.Secret);

            var expiresOn = DateTime.UtcNow.AddDays(_appSettings.TokenExpirationInDays);

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.Name, user.Id),
                    new Claim(ApplicationDbContext.AdminRoleName.ToLowerInvariant(),
                        (await _userManager.IsInRoleAsync(user, ApplicationDbContext.AdminRoleName)).ToString()),
                }),
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
}