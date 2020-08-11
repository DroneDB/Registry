using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Controllers
{
    [Authorize]
    [ApiController]
    [Route("ddb/share")]
    public class ShareController : ControllerBaseEx
    {
        private readonly IShareManager _shareManager;

        public ShareController(IShareManager shareManager)
        {
            _shareManager = shareManager;
        }


        [HttpPost("init")]
        public async Task<IActionResult> Init(ShareInitDto parameters)
        {
            try
            {
                var token = await _shareManager.Initialize(parameters);

                return Ok(new ShareInitResDto { Token = token });

            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }
        }

        [HttpPost("upload")]
        public async Task<IActionResult> Upload(string token, string path, IFormFile file)
        {
            try
            {

                await using var memory = new MemoryStream();
                await file.CopyToAsync(memory);
                
                await _shareManager.Upload(token, path, memory.ToArray());

                return Ok();

            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }
        }

        [HttpPost("commit")]
        public async Task<IActionResult> Commit(string token)
        {
            try
            {
                
                await _shareManager.Commit(token);

                return Ok();

            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }
        }

    }
}
