using Microsoft.AspNetCore.Http;
using Registry.Ports;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers;

/// <summary>
/// The six collaborators every OGC manager (<see cref="OgcManagerBase"/>) works through, taken as one
/// object so that a manager's own constructor lists only what is specific to it. Registered once in
/// the container (scoped, like the resolver and the catalog it holds).
/// </summary>
public sealed class OgcManagerDependencies(
    IDatasetAccessResolver access,
    IBuildArtifactResolver builds,
    IDdbWrapper ddbWrapper,
    IOgcLayerCatalog layerCatalog,
    IRecordCache records,
    IHttpContextAccessor httpContext)
{
    /// <summary>Resolves and authorizes the dataset of a request.</summary>
    public IDatasetAccessResolver Access { get; } = access;

    /// <summary>Finds the build artifacts of an entry by role.</summary>
    public IBuildArtifactResolver Builds { get; } = builds;

    /// <summary>The native library.</summary>
    public IDdbWrapper DdbWrapper { get; } = ddbWrapper;

    /// <summary>The layers a dataset serves.</summary>
    public IOgcLayerCatalog LayerCatalog { get; } = layerCatalog;

    /// <summary>The distributed record cache (capabilities documents and the like).</summary>
    public IRecordCache Records { get; } = records;

    /// <summary>The ambient request, for the service URLs.</summary>
    public IHttpContextAccessor HttpContext { get; } = httpContext;
}
