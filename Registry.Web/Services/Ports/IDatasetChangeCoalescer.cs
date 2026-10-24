#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Collects the cache invalidations that follow uploads (OGC documents, dataset thumbnail) and
/// runs them once per dataset per window instead of once per uploaded file (UPL-01 C.3).
/// </summary>
public interface IDatasetChangeCoalescer
{
    /// <summary>Files were added to the dataset (they are already in its index).</summary>
    void FilesAdded(string orgSlug, string dsSlug, IEnumerable<string> paths);
}
