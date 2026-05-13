using System.Threading.Tasks;

namespace Registry.Web.Services.Ports;

public interface IWmtsManager
{
    Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug, string? folderPath = null);
    Task<byte[]> GetTileAsync(string orgSlug, string dsSlug, string layerName,
        string tileMatrixSet, int z, int x, int y, string format);
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
        double[]? bbox, string? bboxCrs, int count, int startIndex, string outputFormat);
}

public interface IWcsManager
{
    Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug, string? folderPath = null);
    Task<string> DescribeCoverageAsync(string orgSlug, string dsSlug, string coverageId);
    Task<byte[]> GetCoverageAsync(string orgSlug, string dsSlug, string coverageId,
        double[]? subsetBbox, string format);
}

public interface IOgcApiFeaturesManager
{
    Task<Models.DTO.Ogc.OgcApiLandingDto> GetLandingAsync(string orgSlug, string dsSlug, string baseUrl);
    Task<Models.DTO.Ogc.OgcConformanceDto> GetConformanceAsync();
    Task<Models.DTO.Ogc.OgcApiCollectionsDto> GetCollectionsAsync(string orgSlug, string dsSlug, string baseUrl);
    Task<Models.DTO.Ogc.OgcApiCollectionDto?> GetCollectionAsync(string orgSlug, string dsSlug, string collectionId, string baseUrl);
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
