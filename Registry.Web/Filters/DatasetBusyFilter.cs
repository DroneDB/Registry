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
/// The one gate of the busy datasets (registered once with MVC): an action whose route names a dataset
/// (<see cref="RoutesHelper.OrganizationSlugKey"/> and <see cref="RoutesHelper.DatasetSlugKey"/>) is refused with
/// <see cref="DatasetBusyException"/> (409) while a task imports the dataset or rescans its index
/// (<see cref="IDatasetActivity"/>), unless it is marked <see cref="AllowWhileDatasetBusyAttribute"/>. A resource filter, so
/// a refused upload is answered before its body is read. Only a caller who may read the dataset learns that it is busy:
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
        if (!context.ActionDescriptor.EndpointMetadata.OfType<AllowWhileDatasetBusyAttribute>().Any() &&
            context.RouteData.Values.TryGetValue(RoutesHelper.OrganizationSlugKey, out var org) &&
            context.RouteData.Values.TryGetValue(RoutesHelper.DatasetSlugKey, out var ds) &&
            org is string orgSlug && ds is string dsSlug)
        {
            var ct = context.HttpContext.RequestAborted;
            if (await _activity.GetAsync(orgSlug, dsSlug, ct) is { } busy)
            {
                await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);
                throw DatasetActivity.Refusal(orgSlug, dsSlug, busy);
            }
        }

        await next();
    }
}
