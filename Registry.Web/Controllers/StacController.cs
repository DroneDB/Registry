using System;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Controllers;

/// <summary>
/// Controller for STAC (SpatioTemporal Asset Catalog) API endpoints.
/// Provides access to datasets in STAC format for interoperability with geospatial tools.
/// </summary>
[ApiController]
[Tags("STAC")]
[Produces("application/json")]
public class StacController : ControllerBaseEx
{
    private readonly IStacManager _stacManager;
    private readonly ILogger<StacController> _logger;

    private const string MediaTypeJson = "application/json";
    private const string MediaTypeGeoJson = "application/geo+json";

    public StacController(IStacManager stacManager,
        ILogger<StacController> logger)
    {
        _stacManager = stacManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets the STAC API landing page (root catalog) with conformance classes and
    /// links to collections, conformance, and item search endpoints.
    /// </summary>
    /// <returns>The STAC API landing page.</returns>
    [HttpGet("/stac", Name = nameof(StacController) + "." + nameof(GetCatalog))]
    [ProducesResponseType(typeof(StacCatalogDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetCatalog()
    {
        try
        {
            _logger.LogDebug("Stac controller GetCatalog()");

            return StacContent(await _stacManager.GetLandingPage(), MediaTypeJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Stac controller GetCatalog()");

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets the STAC API conformance declaration.
    /// </summary>
    [HttpGet("/stac/conformance", Name = nameof(StacController) + "." + nameof(GetConformance))]
    [ProducesResponseType(typeof(StacConformanceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public IActionResult GetConformance()
    {
        try
        {
            _logger.LogDebug("Stac controller GetConformance()");

            return StacContent(_stacManager.GetConformance(), MediaTypeJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Stac controller GetConformance()");

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Lists all STAC collections the current user can access.
    /// </summary>
    [HttpGet("/stac/collections", Name = nameof(StacController) + "." + nameof(GetCollections))]
    [ProducesResponseType(typeof(StacCollectionsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetCollections()
    {
        try
        {
            _logger.LogDebug("Stac controller GetCollections()");

            return StacContent(await _stacManager.GetCollections(), MediaTypeJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Stac controller GetCollections()");

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets a single STAC collection for a dataset.
    /// </summary>
    [HttpGet("/stac/collections/{orgSlug}/{dsSlug}", Name = nameof(StacController) + "." + nameof(GetCollection))]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetCollection(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug)
    {
        try
        {
            _logger.LogDebug("Stac controller GetCollection('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

            return StacContent(await _stacManager.GetCollection(orgSlug, dsSlug), MediaTypeJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Stac controller GetCollection()");

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets the items (STAC ItemCollection) of a dataset collection, optionally filtered by bbox and datetime.
    /// </summary>
    [HttpGet("/stac/collections/{orgSlug}/{dsSlug}/items",
        Name = nameof(StacController) + "." + nameof(GetCollectionItems))]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetCollectionItems(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromQuery] string bbox = null,
        [FromQuery] string datetime = null,
        [FromQuery] int? limit = null,
        [FromQuery] int? offset = null)
    {
        try
        {
            _logger.LogDebug("Stac controller GetCollectionItems('{OrgSlug}/{DsSlug}')", orgSlug, dsSlug);

            if (!TryParseBbox(bbox, out var parsedBbox))
                return BadRequest(new ErrorResponse(BboxError));

            if (limit is < 1)
                return BadRequest(new ErrorResponse(LimitError));

            if (!IsValidDatetime(datetime))
                return BadRequest(new ErrorResponse(DatetimeError));

            return StacContent(
                await _stacManager.GetCollectionItems(orgSlug, dsSlug, parsedBbox, datetime, limit, offset),
                MediaTypeGeoJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Stac controller GetCollectionItems()");

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets a single STAC item (feature) of a dataset collection.
    /// </summary>
    [HttpGet("/stac/collections/{orgSlug}/{dsSlug}/items/{featureId}",
        Name = nameof(StacController) + "." + nameof(GetCollectionItem))]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetCollectionItem(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string featureId)
    {
        try
        {
            _logger.LogDebug("Stac controller GetCollectionItem('{OrgSlug}/{DsSlug}', {FeatureId})",
                orgSlug, dsSlug, featureId);

            return StacContent(await _stacManager.GetCollectionItem(orgSlug, dsSlug, featureId), MediaTypeGeoJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Stac controller GetCollectionItem()");

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// STAC API Item Search (GET). Searches items across all accessible collections.
    /// </summary>
    [HttpGet("/stac/search", Name = nameof(StacController) + "." + nameof(SearchGet))]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SearchGet(
        [FromQuery] string bbox = null,
        [FromQuery] string intersects = null,
        [FromQuery] string datetime = null,
        [FromQuery] int? limit = null,
        [FromQuery] string collections = null,
        [FromQuery] string ids = null,
        [FromQuery] string token = null)
    {
        try
        {
            _logger.LogDebug("Stac controller SearchGet()");

            var hasBbox = !string.IsNullOrWhiteSpace(bbox);
            var hasIntersects = !string.IsNullOrWhiteSpace(intersects);

            if (hasBbox && hasIntersects)
                return BadRequest(new ErrorResponse(BboxIntersectsError));

            if (!TryParseBbox(bbox, out var parsedBbox))
                return BadRequest(new ErrorResponse(BboxError));

            if (limit is < 1)
                return BadRequest(new ErrorResponse(LimitError));

            if (!IsValidDatetime(datetime))
                return BadRequest(new ErrorResponse(DatetimeError));

            JToken parsedIntersects = null;
            if (hasIntersects)
            {
                try
                {
                    parsedIntersects = JToken.Parse(intersects);
                }
                catch (JsonException)
                {
                    return BadRequest(new ErrorResponse("intersects must be a valid GeoJSON geometry"));
                }
            }

            var request = new StacSearchRequestDto
            {
                Bbox = parsedBbox,
                Intersects = parsedIntersects,
                Datetime = datetime,
                Limit = limit,
                Token = token,
                Collections = string.IsNullOrWhiteSpace(collections)
                    ? null
                    : collections.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Ids = string.IsNullOrWhiteSpace(ids)
                    ? null
                    : ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            };

            return StacContent(await _stacManager.Search(request), MediaTypeGeoJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Stac controller SearchGet()");

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// STAC API Item Search (POST). Searches items across all accessible collections.
    /// </summary>
    [HttpPost("/stac/search", Name = nameof(StacController) + "." + nameof(SearchPost))]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SearchPost([FromBody] StacSearchRequestDto request)
    {
        try
        {
            _logger.LogDebug("Stac controller SearchPost()");

            if (request?.Bbox is { Length: > 0 } && request.Intersects is not null)
                return BadRequest(new ErrorResponse(BboxIntersectsError));

            if (request?.Bbox is { Length: > 0 } && !IsValidBboxArray(request.Bbox))
                return BadRequest(new ErrorResponse(BboxError));

            if (request?.Limit is < 1)
                return BadRequest(new ErrorResponse(LimitError));

            if (!IsValidDatetime(request?.Datetime))
                return BadRequest(new ErrorResponse(DatetimeError));

            return StacContent(await _stacManager.Search(request), MediaTypeGeoJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Stac controller SearchPost()");

            return ExceptionResult(ex);
        }
    }

    private const string BboxError =
        "bbox must be 4 or 6 comma-separated numbers with minY <= maxY (minX,minY,maxX,maxY or minX,minY,minZ,maxX,maxY,maxZ)";

    private const string LimitError = "limit must be a positive integer";

    private const string BboxIntersectsError = "bbox and intersects are mutually exclusive";

    private const string DatetimeError =
        "datetime must be a valid RFC 3339 datetime or interval (e.g. 2020-01-01T00:00:00Z or start/end)";

    // RFC 3339 date-time: case-insensitive 'T'/'Z', optional fractional seconds (any length),
    // and a mandatory timezone designator ("Z" or "+/-HH:MM").
    private static readonly System.Text.RegularExpressions.Regex Rfc3339Regex =
        new(@"^(\d{4})-(\d{2})-(\d{2})[Tt](\d{2}):(\d{2}):(\d{2})(\.\d+)?([Zz]|[+-]\d{2}:\d{2})$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool TryValidateInstant(string value, out System.DateTimeOffset? parsed)
    {
        parsed = null;

        var match = Rfc3339Regex.Match(value);
        if (!match.Success)
            return false;

        var month = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        var hour = int.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture);
        var second = int.Parse(match.Groups[6].Value, System.Globalization.CultureInfo.InvariantCulture);

        if (month is < 1 or > 12)
            return false;
        if (day is < 1 or > 31)
            return false;
        if (hour > 23)
            return false;
        if (minute > 59)
            return false;
        if (second > 60) // allow leap second
            return false;

        // .NET supports at most 7 fractional digits; truncate any extra for the comparison parse.
        var normalized = value;
        var fraction = match.Groups[7].Value;
        if (fraction.Length > 8)
            normalized = value[..match.Groups[7].Index] + fraction[..8] + match.Groups[8].Value;

        if (System.DateTimeOffset.TryParse(normalized,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dto))
            parsed = dto;

        return true;
    }

    /// <summary>
    /// Validates a STAC / OGC API Features datetime query value: a single RFC 3339 instant,
    /// or an interval "start/end" where exactly one side may be open (empty string or "..").
    /// </summary>
    private static bool IsValidDatetime(string datetime)
    {
        if (string.IsNullOrWhiteSpace(datetime))
            return true;

        if (!datetime.Contains('/'))
            return TryValidateInstant(datetime, out _);

        var parts = datetime.Split('/');
        if (parts.Length != 2)
            return false;

        var start = parts[0];
        var end = parts[1];

        var startOpen = start.Length == 0 || start == "..";
        var endOpen = end.Length == 0 || end == "..";

        if (startOpen && endOpen)
            return false;

        System.DateTimeOffset? startDt = null, endDt = null;

        if (!startOpen && !TryValidateInstant(start, out startDt))
            return false;

        if (!endOpen && !TryValidateInstant(end, out endDt))
            return false;

        if (startDt.HasValue && endDt.HasValue && startDt.Value > endDt.Value)
            return false;

        return true;
    }

    private ContentResult StacContent(object value, string contentType)
    {
        return new ContentResult
        {
            Content = JsonConvert.SerializeObject(value),
            ContentType = contentType,
            StatusCode = StatusCodes.Status200OK
        };
    }

    private static bool IsValidBboxArray(double[] bbox)
    {
        if (bbox is not ({ Length: 4 } or { Length: 6 }))
            return false;

        var minY = bbox[1];
        var maxY = bbox.Length == 4 ? bbox[3] : bbox[4];
        return minY <= maxY;
    }

    private static bool TryParseBbox(string bbox, out double[] result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(bbox))
            return true;

        var parts = bbox.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4 && parts.Length != 6)
            return false;

        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out values[i]))
                return false;
        }

        if (!IsValidBboxArray(values))
            return false;

        result = values;
        return true;
    }

    /// <summary>
    /// Catches single-segment collection ids (e.g. /stac/collections/non-existent) and returns 404,
    /// as required by the STAC API / OGC API Features conformance tests.
    /// </summary>
    [HttpGet("/stac/collections/{collectionId}",
        Name = nameof(StacController) + "." + nameof(GetCollectionNotFound))]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public IActionResult GetCollectionNotFound([FromRoute, Required] string collectionId)
    {
        return NotFound(new ErrorResponse($"Collection '{collectionId}' not found"));
    }

    /// <summary>
    /// Gets a STAC child resource (Collection or Item) for a specific dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="pathBase64">Optional Base64-encoded path to a specific item within the dataset.</param>
    /// <returns>A STAC Collection or Item depending on the path. Returns a Collection when no path is specified, or an Item when a specific asset path is provided.</returns>
    [HttpGet("/orgs/{orgSlug}/ds/{dsSlug}/stac/{pathBase64?}",
        Name = nameof(StacController) + "." + nameof(GetStacChild))]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetStacChild(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string dsSlug,
        [FromRoute] string pathBase64 = null)
    {
        try
        {
            _logger.LogDebug("Stac controller GetStacChild()");

            var path = pathBase64 != null ? Encoding.UTF8.GetString(Convert.FromBase64String(pathBase64)) : null;

            return Ok(await _stacManager.GetStacChild(orgSlug, dsSlug, path));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Stac controller GetStacChild()");

            return ExceptionResult(ex);
        }
    }
}