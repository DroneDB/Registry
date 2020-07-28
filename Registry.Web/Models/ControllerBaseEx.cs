using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Exceptions;
using Registry.Web.Services.Ports;

namespace Registry.Web.Models
{
    public class ControllerBaseEx : ControllerBase
    {

        protected IActionResult ExceptionResult(Exception ex)
        {
            return ex switch
            {
                UnauthorizedException e => Unauthorized(e.Message),
                ConflictException e => Conflict(e.Message),
                NotFoundException e => NotFound(e.Message),
                BadRequestException e => BadRequest(e.Message),
                _ => BadRequest(ex.Message)
            };
        }
 
    }
}
