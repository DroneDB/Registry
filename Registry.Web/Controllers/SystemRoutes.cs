namespace Registry.Web.Controllers;

/// <summary>
/// The route names of the <c>/sys</c> endpoints. They kept the <c>SystemController.</c> prefix when RQ-12 split
/// that controller (maintenance, build maintenance, remote import, processing nodes, features), so a link built
/// from a route name does not change.
/// </summary>
public static class SystemRoutes
{
    public const string Prefix = nameof(SystemController) + ".";
}
