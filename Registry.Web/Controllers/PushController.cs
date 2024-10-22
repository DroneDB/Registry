using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

[ApiController]
[Route(RoutesHelper.OrganizationsRadix + "/" +
       RoutesHelper.OrganizationSlug + "/" +
       RoutesHelper.DatasetRadix + "/" +
       RoutesHelper.DatasetSlug + "/" +
       RoutesHelper.PushRadix)]
public class PushController : ControllerBaseEx
{
    private readonly IPushManager _pushManager;
    private readonly ILogger<PushController> _logger;

    public PushController(IPushManager pushManager, ILogger<PushController> logger)
    {
        _pushManager = pushManager;
        _logger = logger;
    }

    [HttpPost("init")]
    public async Task<IActionResult> Init([FromRoute] string orgSlug, [FromRoute] string dsSlug, [FromForm] string checksum, [FromForm(Name="stamp")] string stampJson)
    {
        try
        {
            _logger.LogDebug("Push controller Init('{OrgSlug}', '{DsSlug}', '{Checksum}', '{StampJson}')", orgSlug, dsSlug, checksum, stampJson);

            var stamp = JsonConvert.DeserializeObject<StampDto>(stampJson);

            var res = await _pushManager.Init(orgSlug, dsSlug, checksum, stamp);

            return Ok(res);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Push controller Init('{OrgSlug}', '{DsSlug}', '{Checksum}', '{StampJson}')", orgSlug, dsSlug, checksum, stampJson);

            return ExceptionResult(ex);
        }


    }

    [DisableRequestSizeLimit]
    [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue)]
    [HttpPost("upload")]
    public async Task<IActionResult> Upload([FromRoute] string orgSlug, [FromRoute] string dsSlug, [FromForm] string path, [FromForm] string token, IFormFile file)
    {
        try
        {
            _logger.LogDebug("Push controller Upload('{OrgSlug}', '{DsSlug}', '{Token}', '{FileName}')", orgSlug, dsSlug, token, file?.FileName);

            if (file == null)
                throw new ArgumentException("No file uploaded");

            await using var stream = file.OpenReadStream();
            await _pushManager.Upload(orgSlug, dsSlug, path, token, stream);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Push controller Upload('{OrgSlug}', '{DsSlug}', '{Token}', '{FileName}')", orgSlug, dsSlug, token, file?.FileName);

            return ExceptionResult(ex);
        }
    }


    [DisableRequestSizeLimit]
    [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue)]
    [HttpPost("meta")]
    public async Task<IActionResult> Meta([FromRoute] string orgSlug, [FromRoute] string dsSlug, [FromForm] string token, [FromForm] string meta)
    {
        try
        {
            _logger.LogDebug("Push controller Meta('{OrgSlug}', '{DsSlug}', '{Token}', '{Meta}')", orgSlug, dsSlug, token, meta);

            if (meta == null)
                throw new ArgumentException("No meta JSON in form");

            await _pushManager.SaveMeta(orgSlug, dsSlug, token, meta);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Push controller Meta('{OrgSlug}', '{DsSlug}', '{Token}', '{Meta}')", orgSlug, dsSlug, token, meta);

            return ExceptionResult(ex);
        }
    }

    [HttpPost("commit")]
    public async Task<IActionResult> Commit([FromRoute] string orgSlug, [FromRoute] string dsSlug, [FromForm] string token)
    {
        try
        {
            _logger.LogDebug("Push controller Commit('{OrgSlug}', '{DsSlug}', '{Token}')", orgSlug, dsSlug, token);

            await _pushManager.Commit(orgSlug, dsSlug, token);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Push controller Commit('{OrgSlug}', '{DsSlug}', '{Token}')", orgSlug, dsSlug, token);

            return ExceptionResult(ex);
        }
    }


}