using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;

namespace Registry.Web.Services.Adapters
{
    public class UsersManager : IUsersManager
    {

        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly IAuthManager _authManager;
        private readonly IOrganizationsManager _organizationsManager;
        private readonly IUtils _utils;
        private readonly ILogger<UsersManager> _logger;
        private readonly SignInManager<User> _signInManager;
        private readonly UserManager<User> _userManager;
        private readonly AppSettings _appSettings;

        public UsersManager(
            IOptions<AppSettings> appSettings,
            SignInManager<User> signInManager,
            UserManager<User> userManager,
            RoleManager<IdentityRole> roleManager,
            IAuthManager authManager,
            IOrganizationsManager organizationsManager,
            IUtils utils,
            ILogger<UsersManager> logger)
        {
            _roleManager = roleManager;
            _authManager = authManager;
            _organizationsManager = organizationsManager;
            _utils = utils;
            _logger = logger;
            _signInManager = signInManager;
            _userManager = userManager;
            _appSettings = appSettings.Value;

        }

        public async Task<AuthenticateResponse> Authenticate(string userName, string password)
        {
            var user = await _userManager.FindByNameAsync(userName);

            SignInResult res;
            if (user == null)
            {
                user = new User { UserName = userName };
                res = await _signInManager.CheckPasswordSignInAsync(user, password, false);

                if (!res.Succeeded) return null;

                await _CreateUserInternal(userName, null, password);
                user = await _userManager.FindByNameAsync(userName);
            }
            else
            {
                res = await _signInManager.CheckPasswordSignInAsync(user, password, false);

                if (!res.Succeeded) return null;
            }
            
            // authentication successful so generate jwt token
            var tokenDescriptor = await GenerateJwtToken(user);

            return new AuthenticateResponse(user, tokenDescriptor.Token, tokenDescriptor.ExpiresOn);
        }

        public async Task CreateUser(string userName, string email, string password)
        {

            if (!await _authManager.IsUserAdmin())
                throw new UnauthorizedException("Only admins can create new users");

            var user = await _userManager.FindByNameAsync(userName);

            if (user != null)
                throw new InvalidOperationException("User already exists");

            await _CreateUserInternal(userName, email, password);
        }

        private async Task _CreateUserInternal(string userName, string email, string password)
        {
            var user = new User
            {
                UserName = userName,
                Email = email
            };

            var res = await _userManager.CreateAsync(user, password);

            if (!res.Succeeded)
            {
                var errors = string.Join(";", res.Errors.Select(item => $"{item.Code} - {item.Description}"));
                _logger.LogWarning("Error in creating user");
                _logger.LogWarning(errors);

                throw new InvalidOperationException("Error in creating user");
            }

            // Create a default organization for the user
            await CreateUserDefaultOrganization(user);
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

        public async Task ChangePassword(string userName, string currentPassword, string newPassword)
        {
            var user = await _userManager.FindByNameAsync(userName);

            if (user == null)
                throw new BadRequestException("User does not exist");

            var res = await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);

            if (!res.Succeeded)
            {
                var errors = string.Join(";", res.Errors.Select(item => $"{item.Code} - {item.Description}"));
                _logger.LogWarning("Error in changing user password");
                _logger.LogWarning(errors);

                throw new InvalidOperationException("Cannot change user password: " + errors);

            }

        }

        public async Task<AuthenticateResponse> Refresh()
        {
            var user = await _authManager.GetCurrentUser();

            if (user == null)
                throw new BadRequestException("User does not exist");

            var tokenDescriptor = await GenerateJwtToken(user);

            return new AuthenticateResponse(user, tokenDescriptor.Token, tokenDescriptor.ExpiresOn);

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
                _logger.LogWarning("Error in deleting user");
                _logger.LogWarning(errors);

                throw new InvalidOperationException("Cannot delete user: " + errors);
            }

        }

        public async Task<IEnumerable<UserDto>> GetAll()
        {
            if (!await _authManager.IsUserAdmin())
                throw new UnauthorizedException("User is not admin");

            var query = from user in _userManager.Users
                        select new UserDto
                        {
                            Email = user.Email,
                            UserName = user.UserName,
                            Id = user.Id
                        };


            return query.ToArray();

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
                    new Claim(ApplicationDbContext.AdminRoleName.ToLowerInvariant(), (await _userManager.IsInRoleAsync(user, ApplicationDbContext.AdminRoleName)).ToString()),
                }),
                Expires = expiresOn,
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
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
