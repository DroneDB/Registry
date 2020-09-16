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
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
{
    public class UsersManager : IUsersManager
    {

        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly IAuthManager _authManager;
        private readonly SignInManager<User> _signInManager;
        private readonly UserManager<User> _usersManager;
        private readonly AppSettings _appSettings;

        public UsersManager(
            IOptions<AppSettings> appSettings,
            SignInManager<User> signInManager,
            UserManager<User> usersManager,
            RoleManager<IdentityRole> roleManager,
            IAuthManager authManager)
        {
            _roleManager = roleManager;
            _authManager = authManager;
            _signInManager = signInManager;
            _usersManager = usersManager;
            _appSettings = appSettings.Value;

        }

        public async Task<AuthenticateResponse> Authenticate(AuthenticateRequest model)
        {
            var res = await GetAutentication(model);

            return res;
        }

        public async Task<AuthenticateResponse> GetAutentication(AuthenticateRequest model)
        {
            var user = await _usersManager.FindByNameAsync(model.Username);

            if (user == null) return null;

            var res = await _signInManager.CheckPasswordSignInAsync(user, model.Password, false);

            if (!res.Succeeded) return null;

            // authentication successful so generate jwt token
            var token = await GenerateJwtToken(user);

            return new AuthenticateResponse(user, token);
        }

        public async Task<IEnumerable<UserDto>> GetAll()
        {
            if (!await _authManager.IsUserAdmin()) 
                throw new UnauthorizedException("User is not admin");

            var query = from user in _usersManager.Users
                select new UserDto
                {
                    Email = user.Email,
                    UserName = user.UserName,
                    Id = user.Id
                };


            return query.ToArray();

        }
        private async Task<string> GenerateJwtToken(User user)
        {
            // generate token
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.ASCII.GetBytes(_appSettings.Secret);

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.Name, user.Id),
                    new Claim(ApplicationDbContext.AdminRoleName.ToLowerInvariant(), (await _usersManager.IsInRoleAsync(user, ApplicationDbContext.AdminRoleName)).ToString()),
                }),
                Expires = DateTime.UtcNow.AddDays(_appSettings.TokenExpirationInDays),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
            };
            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }

    }
}
