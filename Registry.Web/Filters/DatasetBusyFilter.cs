using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Filters;
using Registry.Web.Attributes;
using Registry.Web.Exceptions;
using Registry.Web.Services.Datasets;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Filters;

/// <summary>
/// The one gate of the busy datasets, on every action of a dataset (<see cref="DatasetBusyConvention"/>): the request is
/// refused with <see cref="DatasetBusyException"/> (409) while a task imports the dataset its route names
/// (<see cref="RoutesHelper.OrganizationSlugKey"/> and <see cref="RoutesHelper.DatasetSlugKey"/>) or rescans its index
/// (<see cref="IDatasetActivity"/>); a dataset an action names in its form (<see cref="DatasetInFormAttribute"/>) is
/// gated the same way. A resource filter, so a refused upload is answered before its body is read. Only a caller who may read the dataset learns that it is busy:
/// any other gets the answer of the access check (401, 403 or 404), as without the task.
/// </summary>
public sealed class DatasetBusyFilter : IAsyncResourceFilter
{
    private readonly IDatasetActivity _activity;
    private readonly IDatasetAccessResolver _access;

    public DatasetBusyFilter(IDatasetActivity activity, IDatasetAccessResolver access)
    {
        _activity = activity;
        _access = access;
    }

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var ct = context.HttpContext.RequestAborted;
        if (RouteDataset(context) is { } route)
            await EnsureNotBusyAsync(route.Org, route.Ds, ct);

        foreach (var named in context.ActionDescriptor.EndpointMetadata.OfType<DatasetInFormAttribute>())
            if (await FormDatasetAsync(context, named, ct) is { } inForm)
                await EnsureNotBusyAsync(inForm.Org, inForm.Ds, ct);

        await next();
    }

    // The dataset of the route, when the route names one
    private static (string Org, string Ds)? RouteDataset(ResourceExecutingContext context) =>
        context.RouteData.Values.TryGetValue(RoutesHelper.OrganizationSlugKey, out var org) &&
        context.RouteData.Values.TryGetValue(RoutesHelper.DatasetSlugKey, out var ds) &&
        org is string orgSlug && ds is string dsSlug
            ? (orgSlug, dsSlug)
            : null;

    // The dataset a form names (read once: model binding reuses the parsed form), when both fields are there; slugs are
    // stored in lower case, as the route values are lowered
    private static async Task<(string Org, string Ds)?> FormDatasetAsync(ResourceExecutingContext context,
        DatasetInFormAttribute named, System.Threading.CancellationToken ct)
    {
        var request = context.HttpContext.Request;
        if (!request.HasFormContentType)
            return null;

        var form = await request.ReadFormAsync(ct);
        string org = form[named.OrganizationField].ToString(), ds = form[named.DatasetField].ToString();
        return string.IsNullOrWhiteSpace(org) || string.IsNullOrWhiteSpace(ds)
            ? null
            : (org.ToLowerInvariant(), ds.ToLowerInvariant());
    }

    // Only a caller who may read the dataset learns that it is busy
    private async Task EnsureNotBusyAsync(string orgSlug, string dsSlug, System.Threading.CancellationToken ct)
    {
        if (await _activity.GetAsync(orgSlug, dsSlug, ct) is not { } busy)
            return;

        await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);
        throw DatasetActivity.Refusal(orgSlug, dsSlug, busy);
    }
}
