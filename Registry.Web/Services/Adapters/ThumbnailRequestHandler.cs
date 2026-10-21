using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Handles <c>POST /generate-thumbnail</c> on the ThumbnailGenerator host. The caller must present the
/// shared token in <see cref="ThumbnailHostContract.TokenHeaderName"/> (401 otherwise) and the
/// file must lie under one of the allowed roots, the datasets and temp folders (403 otherwise).
/// </summary>
internal sealed class ThumbnailRequestHandler
{
    public const string Route = "/generate-thumbnail";

    private readonly string _token;
    private readonly string[] _allowedRoots;
    private readonly IDdbWrapper _ddbWrapper;
    private readonly ILogger _logger;

    /// <summary>
    /// The thumbnail generator host's handler: the shared token and the readable roots (the datasets and
    /// temp folders) come from the options (the host's options validation refuses to start without a token).
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public ThumbnailRequestHandler(IOptions<ThumbnailOptions> thumbnails, IOptions<StorageOptions> storage,
        IDdbWrapper ddbWrapper, ILogger<ThumbnailRequestHandler> logger)
        : this(thumbnails.Value.ThumbnailGeneratorToken, [storage.Value.DatasetsPath, storage.Value.TempPath],
            ddbWrapper, logger)
    {
    }

    /// <exception cref="ArgumentException"><paramref name="token"/> is empty (fail closed) or no root is given.</exception>
    public ThumbnailRequestHandler(string token, IEnumerable<string> allowedRoots, IDdbWrapper ddbWrapper,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("The thumbnail generator token is not set.", nameof(token));

        _token = token;
        _allowedRoots = allowedRoots?.Where(root => !string.IsNullOrWhiteSpace(root)).ToArray() ?? [];

        if (_allowedRoots.Length == 0)
            throw new ArgumentException("At least one allowed root is required.", nameof(allowedRoots));

        _ddbWrapper = ddbWrapper ?? throw new ArgumentNullException(nameof(ddbWrapper));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task HandleAsync(HttpContext context)
    {
        // Authenticate before reading the body
        if (!AuthorizationExtensions.TokensMatch(
                context.Request.Headers[ThumbnailHostContract.TokenHeaderName], _token))
        {
            _logger.LogWarning("Thumbnail request from {RemoteIp} rejected: missing or invalid token",
                context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!context.Request.HasFormContentType)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var form = await context.Request.ReadFormAsync();

        if (!form.ContainsKey("path") || !form.ContainsKey("size"))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        string requestedPath = form["path"];
        string size = form["size"];

        if (string.IsNullOrWhiteSpace(requestedPath) || !int.TryParse(size, out var sizeInt) || sizeInt < 1)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!_allowedRoots.Any(root => CommonUtils.IsUnder(root, requestedPath)))
        {
            _logger.LogWarning("Thumbnail request for {FilePath} rejected: outside the allowed roots", requestedPath);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // The checked (normalised) path is the one that is read
        var filePath = Path.GetFullPath(requestedPath);

        if (!File.Exists(filePath))
        {
            _logger.LogWarning("File {FilePath} not found", filePath);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        _logger.LogInformation("Generating thumbnail for {FilePath} with size {SizeInt}", filePath, sizeInt);

        try
        {
            var thumbnail = _ddbWrapper.GenerateThumbnail(filePath, sizeInt);

            if (thumbnail == null || thumbnail.Length == 0)
            {
                _logger.LogWarning("Thumbnail for {FilePath} with size {SizeInt} not found", filePath, sizeInt);
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = _ddbWrapper.ThumbnailMimeType;
            await context.Response.Body.WriteAsync(thumbnail);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while generating thumbnail: {Message}", e.Message);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }
}
