using Registry.Ports;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Registry.Web.Exceptions;
using Registry.Web.Attributes;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// Controller for managing push operations to datasets.
/// </summary>
[ApiController]
[Route(RoutesHelper.OrganizationsRadix + "/" +
       RoutesHelper.OrganizationSlug + "/" +
       RoutesHelper.DatasetRadix + "/" +
       RoutesHelper.DatasetSlug + "/" +
       RoutesHelper.PushRadix)]
[Produces("application/json")]
public class PushController : ControllerBaseEx
{
    private readonly IPushManager _pushManager;
    private readonly FormOptions _formOptions;
    private readonly IFileSystem _fs;

    public PushController(IPushManager pushManager, IOptions<FormOptions> formOptions, IFileSystem fs)
    {
        _pushManager = pushManager;
        _formOptions = formOptions.Value;
        _fs = fs;
    }

    /// <summary>
    /// Initializes a push operation for a dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="checksum">The checksum of the files to push.</param>
    /// <param name="stampJson">The stamp JSON containing file information.</param>
    /// <returns>The push initialization result containing needed files and token.</returns>
    // The stamp lists every entry (~90 bytes each): above ~45k entries it exceeds the default
    // 4 MiB form value limit
    [DisableRequestSizeLimit]
    [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue)]
    [HttpPost("init")]
    [ProducesResponseType(typeof(PushInitResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Init(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm, Required] string checksum,
        [FromForm(Name="stamp"), Required] string stampJson)
    {
        var stamp = JsonConvert.DeserializeObject<StampDto>(stampJson);

        var res = await _pushManager.Init(orgSlug, dsSlug, checksum, stamp);

        return Ok(res);


    }

    /// <summary>
    /// Uploads a file as part of a push operation, by streaming a <c>multipart/form-data</c> request
    /// with the form fields <c>path</c> (destination path) and <c>token</c> (from init) and one file
    /// part. The file is written once, straight to the dataset volume.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <returns>OK if the upload was successful.</returns>
    [DisableRequestSizeLimit]
    [DisableFormValueModelBinding]
    [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue)]
    [HttpPost("upload")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Upload(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug)
    {
        PushUpload upload = null;
        try
        {
            if (!MultipartRequestHelper.IsMultipartContentType(Request.ContentType))
                throw new BadRequestException("Expected a multipart/form-data request");

            var form = await MultipartRequestHelper.ReadSingleFileFormAsync(Request,
                _formOptions.MultipartBoundaryLengthLimit,
                async body =>
                {
                    upload = await _pushManager.StreamUploadAsync(orgSlug, dsSlug, body, Request.ContentLength);
                    return (upload.TempPath, upload.Bytes);
                }, _ => { /* the finally block deletes it */ });

            var path = form.GetField("path");
            var token = form.GetField("token");


            if (upload == null)
                throw new BadRequestException("No file uploaded");

            _pushManager.Upload(upload, path, token);

            return Ok();
        }
        finally
        {
            // Moved into the push folder on success
            _fs.TryDelete(upload?.TempPath);
        }
    }


    /// <summary>
    /// Uploads metadata as part of a push operation.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="token">The push token obtained from init.</param>
    /// <param name="meta">The metadata JSON to upload.</param>
    /// <returns>OK if the metadata was saved successfully.</returns>
    [DisableRequestSizeLimit]
    [RequestFormLimits(ValueLengthLimit = int.MaxValue, MultipartBodyLengthLimit = long.MaxValue)]
    [HttpPost("meta")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Meta(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm, Required] string token,
        [FromForm, Required] string meta)
    {
        if (meta == null)
            throw new BadRequestException("No meta JSON in form");

        await _pushManager.SaveMeta(orgSlug, dsSlug, token, meta);

        return Ok();
    }

    /// <summary>
    /// Commits a push operation, finalizing all uploaded files and metadata.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="token">The push token obtained from init.</param>
    /// <returns>OK if the commit was successful.</returns>
    [HttpPost("commit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Commit(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromForm, Required] string token)
    {
        await _pushManager.Commit(orgSlug, dsSlug, token);

        return Ok();
    }


}