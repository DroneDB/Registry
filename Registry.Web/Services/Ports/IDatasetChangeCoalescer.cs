#nullable enable
using System.Collections.Generic;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Collects the cache invalidations that follow uploads (OGC documents, dataset thumbnail) and
/// runs them once per dataset per window instead of once per uploaded file.
/// </summary>
public interface IDatasetChangeCoalescer
{
    /// <summary>Files were added to the dataset (they are already in its index).</summary>
    void FilesAdded(string orgSlug, string dsSlug, IEnumerable<string> paths);
}
