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

            var err = new ErrorResponse(ex.Message);

            return ex switch
            {
                UnauthorizedException _ => Unauthorized(err),
                ConflictException _ => Conflict(err),
                NotFoundException _ => NotFound(err),
                BadRequestException _ => BadRequest(err),
                _ => BadRequest(err)
            };
        }
 
    }
}
