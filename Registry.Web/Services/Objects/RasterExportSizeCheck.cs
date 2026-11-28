using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Objects;

/// <summary>
/// <see cref="IRasterExportSizeCheck"/> through the scoped <see cref="RasterAnalysisService"/> of a new scope (the
/// caller's access rules apply, as for the direct export). A host without the raster services (a worker) has nothing to
/// check against: the check is the submit's.
/// </summary>
public sealed class RasterExportSizeCheck : IRasterExportSizeCheck
{
    private readonly IServiceScopeFactory _scopes;

    public RasterExportSizeCheck(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
    }

    /// <inheritdoc />
    public async Task EnsureWithinLimitAsync(string orgSlug, string dsSlug, string path, RasterAnalysisOptions? options,
        CancellationToken ct = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<RasterAnalysisService>() is not { } raster)
            return;

        await raster.EnsureExportSizeWithinLimitAsync(orgSlug, dsSlug, path, options ?? RasterAnalysisOptions.None, ct);
    }
}
