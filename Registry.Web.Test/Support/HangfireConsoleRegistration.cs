using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Hangfire.Dashboard;

namespace Registry.Web.Test.Support;

/// <summary>
/// Hangfire.Console's <c>UseConsole</c> is a once-per-process registration: it throws
/// "Console is already initialized" when its dashboard route is already present in the static
/// <see cref="DashboardRoutes.Routes"/>. Production boots one web host per process, but tests boot
/// one per <see cref="RegistryWebFactory"/>, so the route is cleared before each boot and
/// re-registered by the new host. Hangfire.Console itself detects the route through the same
/// private <c>_dispatchers</c> list, so this follows exactly the state it checks.
/// </summary>
internal static class HangfireConsoleRegistration
{
    private const string ConsoleRoutePrefix = "/console/";

    private static readonly FieldInfo DispatchersField =
        typeof(RouteCollection).GetField("_dispatchers", BindingFlags.Instance | BindingFlags.NonPublic);

    public static void Reset()
    {
        if (DispatchersField?.GetValue(DashboardRoutes.Routes) is not IList dispatchers)
            throw new InvalidOperationException(
                "Hangfire RouteCollection layout changed: cannot reset the Hangfire.Console registration");

        lock (dispatchers)
        {
            for (var i = dispatchers.Count - 1; i >= 0; i--)
            {
                if (dispatchers[i] is ITuple { Length: > 0 } route && route[0] is string template &&
                    template.StartsWith(ConsoleRoutePrefix, StringComparison.Ordinal))
                    dispatchers.RemoveAt(i);
            }
        }
    }
}
