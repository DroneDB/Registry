#nullable enable
using System;
using System.Collections.Generic;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Size and properties of a dataset's index root (what <c>IDDB.GetInfo()</c> returns).
/// </summary>
public sealed record DatasetSummary(long Size, Dictionary<string, object> Properties);

/// <summary>
/// Dataset summaries for code that needs them for many datasets at once (storage quota,
/// dataset listings). A summary is reused while the dataset's index files are unchanged, so
/// writes from any source (Registry, CLI, processing nodes) show up at the next call.
/// Pages that show one dataset keep calling <c>IDDB.GetInfo()</c>.
/// </summary>
public interface IDatasetSummaryProvider
{
    DatasetSummary Get(string orgSlug, Guid internalRef);
}
