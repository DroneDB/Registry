using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Registry.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Controllers
{
    [Authorize]
    [ApiController]
    [Route("[controller]")]
    public class UsersController : ControllerBaseEx
    {

        // TODO: Abstract and test as soon as possible

        private readonly ApplicationDbContext _context;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly IAuthManager _authManager;
        private readonly SignInManager<User> _signInManager;
        private readonly UserManager<User> _usersManager;
        private readonly AppSettings _appSettings;

        public UsersController(
            IOptions<AppSettings> appSettings,
            SignInManager<User> signInManager,
            UserManager<User> usersManager,
            ApplicationDbContext context,
            RoleManager<IdentityRole> roleManager,
            IAuthManager authManager)
        {
            _context = context;
            _roleManager = roleManager;
            _authManager = authManager;
            _signInManager = signInManager;
            _usersManager = usersManager;
            _appSettings = appSettings.Value;

        }

        [AllowAnonymous]
        [HttpPost("authenticate")]
        public async Task<IActionResult> Authenticate([FromForm] AuthenticateRequest model)
        {

            var response = await GetAutentication(model);

            if (response == null)
                return Unauthorized(new ErrorResponse("Unauthorized"));

            return Ok(response);
        }

        private async Task<AuthenticateResponse> GetAutentication(AuthenticateRequest model)
        {
            var user = await _usersManager.FindByNameAsync(model.Username);

            if (user == null) return null;

            var res = await _signInManager.CheckPasswordSignInAsync(user, model.Password, false);

            if (!res.Succeeded) return null;

            // authentication successful so generate jwt token
            var token = await GenerateJwtToken(user);

            return new AuthenticateResponse(user, token);
        }

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {

            if (await _authManager.IsUserAdmin())
            {
                var query = from user in _usersManager.Users
                    select new UserDto
                    {
                        Email = user.Email,
                        UserName = user.UserName,
                        Id = user.Id
                    };


                return Ok(query.ToArray());
            }

            return Unauthorized();
            
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
