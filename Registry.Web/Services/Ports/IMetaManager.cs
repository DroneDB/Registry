using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Metadata CRUD interface (add, set, remove, get, unset, list, dump).
/// </summary>
public interface IMetaManager
{
    public Task<MetaDto> Add(string orgSlug, string dsSlug, string key, string data, string path = null, CancellationToken ct = default);
    public Task<MetaDto> Set(string orgSlug, string dsSlug, string key, string data, string path = null, CancellationToken ct = default);
    public Task<int> Remove(string orgSlug, string dsSlug, string id, CancellationToken ct = default);
    public Task<JsonElement> Get(string orgSlug, string dsSlug, string key, string path = null, CancellationToken ct = default);
    public Task<int> Unset(string orgSlug, string dsSlug, string key, string path = null, CancellationToken ct = default);
    public Task<IEnumerable<MetaListItemDto>> List(string orgSlug, string dsSlug, string path = null, CancellationToken ct = default);
    public Task<IEnumerable<MetaDumpDto>> Dump(string orgSlug, string dsSlug, string ids = null, CancellationToken ct = default);
}