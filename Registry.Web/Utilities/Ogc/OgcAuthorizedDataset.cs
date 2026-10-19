#nullable enable
using Microsoft.AspNetCore.Http;
using Registry.Web.Data.Models;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// The dataset that <see cref="OgcAuthorizationFilter"/> resolved and granted read access to for
/// the current request. OGC managers reuse it instead of resolving and authorizing the dataset a
/// second time; callers outside an authorized OGC request find nothing and check access themselves.
/// </summary>
public static class OgcAuthorizedDataset
{
    private const string ItemKey = "ogc.authorized-dataset";

    private sealed record Grant(string OrgSlug, string DsSlug, Dataset Dataset);

    /// <summary>Records that the request may read <paramref name="dataset"/> ({orgSlug}/{dsSlug}).</summary>
    public static void Store(HttpContext context, string orgSlug, string dsSlug, Dataset dataset)
        => context.Items[ItemKey] = new Grant(orgSlug, dsSlug, dataset);

    /// <summary>The dataset authorized for this request when it is {orgSlug}/{dsSlug}, otherwise null.</summary>
    public static Dataset? Find(HttpContext? context, string orgSlug, string dsSlug)
        => context?.Items[ItemKey] is Grant g && g.OrgSlug == orgSlug && g.DsSlug == dsSlug ? g.Dataset : null;
}
