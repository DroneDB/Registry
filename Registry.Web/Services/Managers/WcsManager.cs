using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Registry.Web.Exceptions;
using Registry.Web.Services.Managers.Wcs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Thin dispatcher implementing <see cref="IWcsManager"/>. Routes each request to the
/// registered <see cref="IWcsProtocolHandler"/> matching the negotiated WCS version
/// (1.0.0 / 1.1.1 / 2.0.1). All version-specific wire-format logic lives in the
/// handlers; this class deliberately holds no XML / KVP knowledge of its own.
/// </summary>
public class WcsManager : IWcsManager
{
    private readonly Dictionary<string, IWcsProtocolHandler> _handlers;

    public WcsManager(IEnumerable<IWcsProtocolHandler> handlers)
    {
        _handlers = handlers.ToDictionary(h => h.Version, System.StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> SupportedVersions => _handlers.Keys.ToList();

    public Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug, string version, string? folderPath = null)
        => Resolve(version).GetCapabilitiesAsync(orgSlug, dsSlug, folderPath);

    public Task<string> DescribeCoverageAsync(string orgSlug, string dsSlug, string version, string coverageId)
        => Resolve(version).DescribeCoverageAsync(orgSlug, dsSlug, coverageId);

    public Task<WcsCoverageResult> GetCoverageAsync(string orgSlug, string dsSlug, string version, IQueryCollection query)
        => Resolve(version).GetCoverageAsync(orgSlug, dsSlug, query);

    private IWcsProtocolHandler Resolve(string version)
    {
        if (!_handlers.TryGetValue(version, out var h))
            throw new OgcException("VersionNegotiationFailed",
                $"WCS version '{version}' is not supported by this server", 400, "VERSION");
        return h;
    }
}
