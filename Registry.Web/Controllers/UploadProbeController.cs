using System.Buffers;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// Upload self-test (UPL-01 A.2): receives a body and discards it, so the Hub can measure the
/// upload throughput through whatever reverse proxy sits in front of the Registry.
/// </summary>
[Authorize]
[ApiController]
[Route(RoutesHelper.SystemRadix)]
[Produces("application/json")]
public class UploadProbeController : ControllerBaseEx
{
    /// <summary>Largest probe body accepted (the Hub sends 8 MB).</summary>
    public const long MaxProbeBytes = 16L * 1024 * 1024;

    private const int BufferSize = 256 * 1024;

    /// <summary>
    /// Reads and discards the request body.
    /// </summary>
    /// <returns>The number of bytes received and the time the server spent reading them.</returns>
    [HttpPost("upload-probe", Name = nameof(UploadProbeController) + "." + nameof(Probe))]
    [EnableRateLimiting(RateLimitPolicies.UploadProbe)]
    [RequestSizeLimit(MaxProbeBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<UploadProbeResultDto>> Probe(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;
        try
        {
            int read;
            while ((read = await Request.Body.ReadAsync(buffer, ct)) > 0)
                total += read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Never cached or compressed: the client times the whole round trip
        Response.Headers.CacheControl = "no-store";
        return new UploadProbeResultDto(total, watch.Elapsed.TotalMilliseconds);
    }
}
