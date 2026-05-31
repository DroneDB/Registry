using System.Collections.Generic;
using System.Threading.Tasks;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Managers.Wcs;

namespace Registry.Web.Services.Ports;

public interface IWmtsManager
{
    Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug,
        IReadOnlyCollection<string>? sections = null, string? folderPath = null);
    Task<byte[]> GetTileAsync(string orgSlug, string dsSlug, string layerName,
        string style, string tileMatrixSet, int z, int x, int y, string format);
}

public interface IWmsManager
{
    Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug, string version, string? folderPath = null);
    Task<byte[]> GetMapAsync(string orgSlug, string dsSlug, string[] layers, string[] styles,
        double[] bbox, string crs, int width, int height, string format, string? bgColor, bool transparent);
    Task<string> GetFeatureInfoAsync(string orgSlug, string dsSlug, string layerName,
        double[] bbox, string crs, int width, int height, int i, int j, string infoFormat);
}

public interface IWfsManager
{
    Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug, string? folderPath = null);
    Task<string> DescribeFeatureTypeAsync(string orgSlug, string dsSlug, string[] typeNames);
    Task<string> GetFeatureAsync(string orgSlug, string dsSlug, string typeName,
        double[]? bbox, string? bboxCrs, int count, int startIndex, string outputFormat,
        string? resourceId = null, string? filterXml = null);
    Task<string> ListStoredQueriesAsync(string orgSlug, string dsSlug);
    Task<string> DescribeStoredQueriesAsync(string orgSlug, string dsSlug, string[] storedQueryIds);
    Task<string> GetFeatureByIdAsync(string orgSlug, string dsSlug, string id, string outputFormat);
    Task<string> GetPropertyValueAsync(string orgSlug, string dsSlug, string typeName,
        string valueReference, int count, int startIndex, string outputFormat,
        string? resourceId = null, string? filterXml = null);
}

public interface IWcsManager
{
    /// <summary>List of WCS protocol versions handled by this manager (highest first).</summary>
    IReadOnlyList<string> SupportedVersions { get; }

    Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug, string version, string? folderPath = null);
    Task<string> DescribeCoverageAsync(string orgSlug, string dsSlug, string version, string coverageId);
    /// <summary>Render the requested coverage. Parameter parsing is per-version and lives
    /// in the dispatched <see cref="Registry.Web.Services.Managers.Wcs.IWcsProtocolHandler"/>;
    /// the manager receives the raw <see cref="Microsoft.AspNetCore.Http.IQueryCollection"/>.</summary>
    Task<WcsCoverageResult> GetCoverageAsync(
        string orgSlug, string dsSlug, string version, Microsoft.AspNetCore.Http.IQueryCollection query);
}

public interface IOgcApiFeaturesManager
{
    Task<OgcApiLandingDto> GetLandingAsync(string orgSlug, string dsSlug, string baseUrl);
    Task<OgcConformanceDto> GetConformanceAsync();
    Task<OgcApiCollectionsDto> GetCollectionsAsync(string orgSlug, string dsSlug, string baseUrl);
    Task<OgcApiCollectionDto?> GetCollectionAsync(string orgSlug, string dsSlug, string collectionId, string baseUrl);
    Task<string> GetItemsAsync(string orgSlug, string dsSlug, string collectionId,
        double[]? bbox, int limit, int offset);
    Task<string> GetItemAsync(string orgSlug, string dsSlug, string collectionId, string featureId);
}

public interface IOgcApiTilesManager
{
    Task<object> GetTileSetsAsync(string orgSlug, string dsSlug, string collectionId, string baseUrl);
    Task<byte[]?> GetTileAsync(string orgSlug, string dsSlug, string collectionId,
        string tileMatrixSet, int z, int x, int y);
}
