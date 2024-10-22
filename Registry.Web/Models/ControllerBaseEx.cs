using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Exceptions;
using Registry.Web.Services.Ports;

namespace Registry.Web.Models;

public class ControllerBaseEx : ControllerBase
{

    protected IActionResult ExceptionResult(Exception ex)
    {

        // Do not retry if the quota is exceeded or the user is not authorized
        var noRetry = ex is QuotaExceededException or UnauthorizedException;
            
        var err = new ErrorResponse(ex.Message, noRetry);
            
        return ex switch
        {
            UnauthorizedException _ => Unauthorized(err),
            ConflictException _ => Conflict(err),
            NotFoundException _ => NotFound(err),
            _ => BadRequest(err)
        };
    }
        
    protected IActionResult ExceptionResult(Exception ex, bool noRetry)
    {
        var err = new ErrorResponse(ex.Message, noRetry);
            
        return ex switch
        {
            UnauthorizedException _ => Unauthorized(err),
            ConflictException _ => Conflict(err),
            NotFoundException _ => NotFound(err),
            _ => BadRequest(err)
        };
    }
 
}