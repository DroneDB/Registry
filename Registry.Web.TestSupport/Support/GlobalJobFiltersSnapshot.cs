using System.Linq;
using Hangfire;
using Hangfire.Common;

namespace Registry.Web.Test.Support;

/// <summary>
/// The Hangfire global filters as they were when the snapshot was taken; <see cref="Restore"/> puts back
/// exactly that list. A host build replaces the global retry policy (one
/// <see cref="AutomaticRetryAttribute"/>, Hangfire's default removed) and adds filters bound to its
/// container, so removing only the added filters would leave the process without its original retry policy.
/// </summary>
public sealed class GlobalJobFiltersSnapshot
{
    private readonly JobFilter[] _filters = GlobalJobFilters.Filters.ToArray();

    public void Restore()
    {
        var filters = GlobalJobFilters.Filters;
        lock (filters)
        {
            filters.Clear();
            foreach (var filter in _filters)
                filters.Add(filter.Instance, filter.Order);
        }
    }
}
