using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Registry.Web.Attributes;
using Registry.Web.Utilities;

namespace Registry.Web.Filters;

/// <summary>
/// Puts the busy-dataset gate (<see cref="DatasetBusyFilter"/>) on every action whose route names a dataset
/// (<see cref="RoutesHelper.OrganizationSlugKey"/> and <see cref="RoutesHelper.DatasetSlugKey"/>) or whose form does
/// (<see cref="DatasetInFormAttribute"/>), once, when the application starts: an action marked
/// <see cref="AllowWhileDatasetBusyAttribute"/> (or <see cref="SkipDatasetBusyCheckAttribute"/>) gets no gate and pays
/// nothing for it, and a new action on a dataset is gated unless it says otherwise.
/// </summary>
public sealed class DatasetBusyConvention : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        foreach (var action in application.Controllers.SelectMany(c => c.Actions))
            if (IsGated(action))
                action.Filters.Add(new TypeFilterAttribute(typeof(DatasetBusyFilter)));
    }

    /// <summary>Whether <paramref name="action"/> gets the gate.</summary>
    public static bool IsGated(ActionModel action) =>
        !action.Attributes.OfType<AllowWhileDatasetBusyAttribute>().Any() &&
        (action.Attributes.OfType<DatasetInFormAttribute>().Any() || Templates(action).Any(NamesADataset));

    /// <summary>Whether a route template names an organization and a dataset (the route values the gate reads).</summary>
    public static bool NamesADataset(string? template) =>
        template != null &&
        template.Contains("{" + RoutesHelper.OrganizationSlugKey, System.StringComparison.Ordinal) &&
        template.Contains("{" + RoutesHelper.DatasetSlugKey, System.StringComparison.Ordinal);

    // The templates of the action combined with those of its controller (an absolute action route stands alone)
    private static string?[] Templates(ActionModel action)
    {
        var controllerRoutes = action.Controller.Selectors.Select(s => s.AttributeRouteModel).DefaultIfEmpty(null)
            .ToArray();
        return action.Selectors
            .SelectMany(s => controllerRoutes.Select(c =>
                AttributeRouteModel.CombineAttributeRouteModel(c, s.AttributeRouteModel)?.Template))
            .ToArray();
    }
}
