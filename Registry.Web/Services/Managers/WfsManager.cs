#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.Managers.Wfs;

namespace Registry.Web.Services.Managers;

/// <summary>
/// WFS 2.0.0 manager (BBOX filter + pagination, no CQL; <see cref="WfsManager"/>, what the controller calls) over
/// the services it is split into: the capabilities and stored queries, the feature types'
/// schema, and the feature queries with their local filter (the way WMS and WCS split by protocol operation).
/// </summary>
public class WfsManager
{
    private readonly WfsCapabilities _capabilities;
    private readonly WfsSchema _schema;
    private readonly WfsFeatureQuery _features;

    public WfsManager(WfsCapabilities capabilities, WfsSchema schema, WfsFeatureQuery features)
    {
        _capabilities = capabilities;
        _schema = schema;
        _features = features;
    }

    public Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug, string? folderPath = null,
        CancellationToken ct = default) => _capabilities.GetCapabilitiesAsync(orgSlug, dsSlug, folderPath, ct);

    public Task<string> DescribeFeatureTypeAsync(string orgSlug, string dsSlug, string[] typeNames,
        CancellationToken ct = default) => _schema.DescribeFeatureTypeAsync(orgSlug, dsSlug, typeNames, ct);

    public Task<string> GetFeatureAsync(string orgSlug, string dsSlug, string typeName,
        double[]? bbox, string? bboxCrs, int count, int startIndex, string outputFormat,
        string? resourceId = null, string? filterXml = null, CancellationToken ct = default) =>
        _features.GetFeatureAsync(orgSlug, dsSlug, typeName, bbox, bboxCrs, count, startIndex, outputFormat,
            resourceId, filterXml, ct);

    public Task<string> ListStoredQueriesAsync(string orgSlug, string dsSlug, CancellationToken ct = default) =>
        _capabilities.ListStoredQueriesAsync(orgSlug, dsSlug, ct);

    public Task<string> DescribeStoredQueriesAsync(string orgSlug, string dsSlug, string[] storedQueryIds,
        CancellationToken ct = default) => _capabilities.DescribeStoredQueriesAsync(orgSlug, dsSlug, storedQueryIds, ct);

    public Task<string> GetFeatureByIdAsync(string orgSlug, string dsSlug, string id, string outputFormat,
        CancellationToken ct = default) => _features.GetFeatureByIdAsync(orgSlug, dsSlug, id, outputFormat, ct);

    public Task<string> GetPropertyValueAsync(string orgSlug, string dsSlug, string typeName,
        string valueReference, int count, int startIndex, string outputFormat,
        string? resourceId = null, string? filterXml = null, CancellationToken ct = default) =>
        _features.GetPropertyValueAsync(orgSlug, dsSlug, typeName, valueReference, count, startIndex, outputFormat,
            resourceId, filterXml, ct);
}
