using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Models;
using Registry.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Registry.Web.Controllers
{
    [Authorize]
    [ApiController]
    [Route("[controller]")]
    public class UsersController : ControllerBase
    {
        private readonly IUserService _userService;

        public UsersController(IUserService userService)
        {
            _userService = userService;
        }

        [AllowAnonymous]
        [HttpPost("authenticate")]
        public IActionResult Authenticate([FromForm]AuthenticateRequest model)
        {
            var response = _userService.Authenticate(model);

            if (response == null)
                return Problem(title: "Username or password is incorrect", statusCode: StatusCodes.Status403Forbidden); 

            return Ok(response);
        }

        [HttpGet]
        public IActionResult GetAll()
        {
            var users = _userService.GetAll();
            return Ok(users);
        }
    }
}
