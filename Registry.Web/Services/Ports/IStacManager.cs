using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// STAC (SpatioTemporal Asset Catalog) API interface for catalog, collection, and search operations.
/// </summary>
public interface IStacManager
{
    Task<JToken> GetStacChild(string orgSlug, string dsSlug, string path = null, CancellationToken ct = default);

    // STAC API 1.0.0 endpoints
    Task<StacCatalogDto> GetLandingPage(CancellationToken ct = default);
    StacConformanceDto GetConformance();
    Task<StacCollectionsDto> GetCollections(int? limit = null, int? offset = null, CancellationToken ct = default);
    Task<JToken> GetCollection(string orgSlug, string dsSlug, CancellationToken ct = default);
    Task<JToken> GetCollectionItems(string orgSlug, string dsSlug, double[] bbox = null,
        string datetime = null, int? limit = null, int? offset = null, CancellationToken ct = default);
    Task<JToken> GetCollectionItem(string orgSlug, string dsSlug, string featureId, CancellationToken ct = default);
    Task<JToken> Search(StacSearchRequestDto request, CancellationToken ct = default);
}
