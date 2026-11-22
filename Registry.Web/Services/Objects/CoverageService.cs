using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Objects;

/// <summary>
/// The photos of a dataset that see a place: the images whose footprint intersects a geometry, best view first. The
/// shape of the request is checked here (<see cref="CoverageGeometry"/>, the limit); the spatial work, and every rule about
/// what a position or a polygon may be, is libddb's.
/// </summary>
public sealed class CoverageService
{
    /// <summary>The fewest images a request can ask for.</summary>
    public const int MinLimit = 1;

    /// <summary>The most images a request can ask for.</summary>
    public const int MaxLimit = 100;

    /// <summary>A request that takes longer than this is logged with its size.</summary>
    public static readonly TimeSpan SlowRequest = TimeSpan.FromSeconds(1);

    private readonly IDatasetAccessResolver _access;
    private readonly INativeWorkGate _gate;
    private readonly ILogger<CoverageService> _logger;

    public CoverageService(IDatasetAccessResolver access, INativeWorkGate gate, ILogger<CoverageService> logger)
    {
        _access = access;
        _gate = gate;
        _logger = logger;
    }

    /// <summary>The images of the dataset whose footprint intersects the geometry, by ascending score.</summary>
    /// <exception cref="BadRequestException">The geometry or the limit is refused, by the checks of the request or by libddb.</exception>
    /// <exception cref="ForbiddenException">The current user may not read the dataset.</exception>
    /// <exception cref="NotFoundException">There is no such dataset.</exception>
    public async Task<List<CoverageResult>> FindAsync(string orgSlug, string dsSlug, CoverageRequestDto? request,
        CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);
        if (request == null)
            throw new BadRequestException("Request body is required");

        var vertices = CoverageGeometry.CountVertices(request.Geometry);
        if (request.Limit is < MinLimit or > MaxLimit)
            throw new InvalidFieldException("limit", $"limit must be between {MinLimit} and {MaxLimit}");

        var geometry = request.Geometry.GetRawText();
        var clock = Stopwatch.StartNew();
        try
        {
            return await _gate.RunAsync(NativeLane.Heavy, () => access.Ddb.FindCoverage(geometry, request.Limit), ct);
        }
        finally
        {
            if (clock.Elapsed > SlowRequest)
                _logger.LogWarning(
                    "Coverage of '{OrgSlug}/{DsSlug}' took {DurationMs} ms for a geometry of {Vertices} positions",
                    orgSlug, dsSlug, (long)clock.Elapsed.TotalMilliseconds, vertices);
        }
    }
}
