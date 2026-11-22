using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Attributes;
using Registry.Web.Controllers;
using Registry.Web.Filters;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The gate of the busy datasets covers every action whose route names a dataset, and only the actions listed here are
/// let through while a task imports or rescans it: a new endpoint on a dataset is gated unless it says otherwise, and the
/// list changes only here, on purpose.
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public sealed class DatasetBusyGateTests
{
    /// <summary>What answers while the dataset is busy: its delete (it stops the task first) and its tasks.</summary>
    private static readonly string[] AllowedWhileBusy =
    [
        $"{nameof(DatasetsController)}.{nameof(DatasetsController.Delete)}",
        $"{nameof(TasksController)}.{nameof(TasksController.List)}",
        $"{nameof(TasksController)}.{nameof(TasksController.Events)}",
        $"{nameof(TasksController)}.{nameof(TasksController.GetStatus)}",
        $"{nameof(TasksController)}.{nameof(TasksController.GetLog)}",
        $"{nameof(TasksController)}.{nameof(TasksController.GetResult)}",
        $"{nameof(TasksController)}.{nameof(TasksController.Cancel)}"
    ];

    private static readonly Type[] Controllers = typeof(Program).Assembly.GetExportedTypes()
        .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract).ToArray();

    // Every action with its route templates (the controller's, combined with the action's; an absolute action route
    // stands alone)
    private static (string Name, MethodInfo Action, string[] Templates)[] Actions() => Controllers
        .SelectMany(c => c.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any() && m.GetCustomAttribute<NonActionAttribute>() == null)
            .Select(m => ($"{c.Name}.{m.Name}", m, Templates(c, m))))
        .ToArray();

    private static string[] Templates(Type controller, MethodInfo action)
    {
        var prefixes = controller.GetCustomAttributes<RouteAttribute>().Select(r => r.Template).DefaultIfEmpty("").ToArray();
        return action.GetCustomAttributes<HttpMethodAttribute>()
            .SelectMany(h => h.Template is { } t && t.StartsWith('/')
                ? [t]
                : prefixes.Select(p => string.IsNullOrEmpty(h.Template) ? p : p + "/" + h.Template))
            .ToArray();
    }

    // The route names the organization and the dataset (the values the gate reads)
    private static bool NamesADataset(string template) =>
        template.Contains("{" + RoutesHelper.OrganizationSlugKey, StringComparison.Ordinal) &&
        template.Contains("{" + RoutesHelper.DatasetSlugKey, StringComparison.Ordinal);

    [Test]
    public void OnlyTheListedActions_AnswerWhileADatasetIsBusy()
    {
        var onDataset = Actions().Where(a => a.Templates.Any(NamesADataset)).ToArray();
        onDataset.Length.ShouldBeGreaterThan(100, "the gate covers the actions of every dataset controller");

        onDataset.Where(a => a.Action.GetCustomAttribute<AllowWhileDatasetBusyAttribute>() != null)
            .Select(a => a.Name).Order().ShouldBe(AllowedWhileBusy.Order());
    }

    [Test]
    public void NoActionOutsideADataset_IsMarked()
    {
        Actions().Where(a => !a.Templates.Any(NamesADataset) &&
                             a.Action.GetCustomAttribute<AllowWhileDatasetBusyAttribute>() != null)
            .Select(a => a.Name).ShouldBeEmpty("the mark means something only on an action whose route names a dataset");
    }

    /// <summary>The gate is a global filter of the API: no controller has to remember it.</summary>
    [Test]
    public void TheGate_IsAGlobalFilter()
    {
        using var factory = new RegistryWebFactory();
        var mvc = factory.Services.GetRequiredService<IOptions<MvcOptions>>().Value;

        mvc.Filters.OfType<TypeFilterAttribute>()
            .Select(f => f.ImplementationType).ShouldContain(typeof(DatasetBusyFilter));
    }
}
