using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Attributes;
using Registry.Web.Controllers;
using Registry.Web.Filters;
using Registry.Web.Models.DTO;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The gate of the busy datasets is on every action that names a dataset, as the running host builds its actions:
/// through its route, through its form (a transfer destination) or through a share batch (its token, or the tag of a new
/// batch). Only the actions listed here are let through while a task imports or rescans the dataset, and only the map tile
/// actions skip the check: a new endpoint on a dataset is gated unless it says otherwise, and the lists change only here,
/// on purpose. The share entry points are refused by the batch store; each one has an HTTP test
/// (<c>ShareBusyHttpTests</c>).
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

    /// <summary>The map tiles: the hot path of every map, read-only, not checked at all.</summary>
    private static readonly string[] NotChecked =
    [
        $"{nameof(ObjectsController)}.{nameof(ObjectsController.GenerateTile)}",
        $"{nameof(RasterAnalysisController)}.{nameof(RasterAnalysisController.GenerateTileEx)}",
        $"{nameof(MvtController)}.{nameof(MvtController.GetTile)}"
    ];

    /// <summary>The actions that reach a dataset through a share batch: the batch store refuses a busy one.</summary>
    private static readonly string[] ThroughAShareBatch =
    [
        $"{nameof(ShareController)}.{nameof(ShareController.Init)}",
        $"{nameof(ShareController)}.{nameof(ShareController.Info)}",
        $"{nameof(ShareController)}.{nameof(ShareController.Upload)}",
        $"{nameof(ShareController)}.{nameof(ShareController.UploadChunked)}",
        $"{nameof(ShareController)}.{nameof(ShareController.Commit)}",
        $"{nameof(ShareController)}.{nameof(ShareController.Rollback)}"
    ];

    private static ControllerActionDescriptor[] _actions = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        using var factory = new RegistryWebFactory();
        _actions = factory.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>().ToArray();
    }

    private static string Name(ControllerActionDescriptor a) => $"{a.ControllerTypeInfo.Name}.{a.MethodInfo.Name}";

    private static bool Gated(ControllerActionDescriptor a) => a.FilterDescriptors
        .Any(f => f.Filter is TypeFilterAttribute { ImplementationType: var type } && type == typeof(DatasetBusyFilter));

    private static bool OnADatasetRoute(ControllerActionDescriptor a) =>
        DatasetBusyConvention.NamesADataset(a.AttributeRouteInfo?.Template);

    [Test]
    public void EveryActionOfADataset_IsGated_ButTheListedOnes()
    {
        var onDataset = _actions.Where(OnADatasetRoute).ToArray();
        onDataset.Length.ShouldBeGreaterThan(100, "the gate covers the actions of every dataset controller");

        onDataset.Where(a => !Gated(a)).Select(Name).Distinct().Order()
            .ShouldBe(AllowedWhileBusy.Concat(NotChecked).Order());
        onDataset.Where(a => a.MethodInfo.GetCustomAttribute<SkipDatasetBusyCheckAttribute>() != null).Select(Name)
            .Distinct().Order().ShouldBe(NotChecked.Order(), "only the map tiles skip the check");
    }

    [Test]
    public void NoActionOutsideADataset_IsMarkedOrGatedByItsRoute()
    {
        _actions.Where(a => !OnADatasetRoute(a) &&
                            a.MethodInfo.GetCustomAttribute<AllowWhileDatasetBusyAttribute>() != null)
            .Select(Name).ShouldBeEmpty("the mark means something only on an action whose route names a dataset");
    }

    /// <summary>A dataset named in a form (besides the route's) is gated too: every such field is declared.</summary>
    [Test]
    public void EveryDatasetNamedInAForm_IsGated()
    {
        foreach (var action in _actions)
        {
            var named = action.MethodInfo.GetCustomAttributes<DatasetInFormAttribute>().Select(d => d.DatasetField)
                .ToArray();
            var formSlugs = action.Parameters
                .Where(p => p.BindingInfo?.BindingSource != BindingSource.Path &&
                            p.Name.EndsWith("DsSlug", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name).ToArray();

            formSlugs.ShouldBe(named, ignoreOrder: true, $"{Name(action)} names a dataset in its form");
            if (named.Length > 0)
                Gated(action).ShouldBeTrue(Name(action));
        }
    }

    /// <summary>
    /// The actions outside a dataset route that take a share batch token or the tag of a new batch are the share entry
    /// points the batch store gates: a new one has to join the list (and its HTTP test).
    /// </summary>
    [Test]
    public void TheActionsThatReachADatasetThroughAShareBatch_AreTheGatedOnes()
    {
        _actions.Where(a => !OnADatasetRoute(a) && a.Parameters.Any(p =>
                (p.Name == "token" && p.BindingInfo?.BindingSource == BindingSource.Path) ||
                p.ParameterType == typeof(ShareInitDto)))
            .Select(Name).Distinct().Order().ShouldBe(ThroughAShareBatch.Order());
    }
}
