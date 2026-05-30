using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

public interface IStacManager
{
    Task<StacCatalogDto> GetCatalog();
    Task<JToken> GetStacChild(string orgSlug, string dsSlug, string path = null);
    Task ClearCache(Dataset ds);

    // STAC API 1.0.0 endpoints
    Task<StacCatalogDto> GetLandingPage();
    StacConformanceDto GetConformance();
    Task<StacCollectionsDto> GetCollections();
    Task<JToken> GetCollection(string orgSlug, string dsSlug);
    Task<JToken> GetCollectionItems(string orgSlug, string dsSlug, double[] bbox = null,
        string datetime = null, int? limit = null, int? offset = null);
    Task<JToken> GetCollectionItem(string orgSlug, string dsSlug, string featureId);
    Task<JToken> Search(StacSearchRequestDto request);
}
