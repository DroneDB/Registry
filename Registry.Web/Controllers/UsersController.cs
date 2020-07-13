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

namespace Registry.Web.Controllers
{
    [Authorize]
    [ApiController]
    [Route("[controller]")]
    public class UsersController : ControllerBase
    {
       
        private readonly ApplicationDbContext _context;
        private readonly SignInManager<User> _signInManager;
        private readonly UserManager<User> _usersManager;
        private readonly AppSettings _appSettings;

        public UsersController(IOptions<AppSettings> appSettings, SignInManager<User> signInManager, UserManager<User> usersManager, ApplicationDbContext context)
        {
            _context = context;
            _signInManager = signInManager;
            _usersManager = usersManager;
            _appSettings = appSettings.Value;

            // If no users in database, let's create the default admin
            if (!_usersManager.Users.Any())
            {
                var user = new User() { Email = "admin@example.com", UserName = "admin" };
                _usersManager.CreateAsync(user, "password").Wait();
            }

        }
        
        [AllowAnonymous]
        [HttpPost("authenticate")]
        public async Task<IActionResult> Authenticate([FromForm]AuthenticateRequest model)
        {

            var response = await GetAutentication(model);

            if (response == null)
                return Problem(title: "Username or password is incorrect", statusCode: StatusCodes.Status403Forbidden); 

            return Ok(response);
        }

        private async Task<AuthenticateResponse> GetAutentication(AuthenticateRequest model)
        {
            var user = await _usersManager.FindByNameAsync(model.Username);

            if (user == null) return null;

            var res = await _signInManager.CheckPasswordSignInAsync(user, model.Password, false);

            if (!res.Succeeded) return null;

            // authentication successful so generate jwt token
            var token = GenerateJwtToken(user);

            return new AuthenticateResponse(user, token);
        }

        [HttpGet]
        public IActionResult GetAll()
        {

            var query = from user in _usersManager.Users
                select new UserDto
                {
                    Email = user.NormalizedEmail,
                    FirstName = user.FirstName,
                    LastName = user.LastName,
                    UserName = user.UserName,
                    Id = user.Id
                };


            return Ok(query.ToArray());
        }
        private string GenerateJwtToken(User user)
        {
            // generate token
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.ASCII.GetBytes(_appSettings.Secret);
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.Name, user.Id.ToString())
                }),
                Expires = DateTime.UtcNow.AddDays(_appSettings.TokenExpirationInDays),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
            };
            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }
    }
}
