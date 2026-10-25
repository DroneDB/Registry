#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Controllers;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The object controllers split from <see cref="ObjectsController"/> keep the request contract.
/// The new controllers enable nullable reference types, and MVC then treats a non-nullable reference-type
/// parameter as required: every parameter that was optional must stay nullable, or a request that leaves
/// it out would become a 400. The route names stay <c>ObjectsController.&lt;Action&gt;</c> (links and
/// <c>CreatedAtRoute</c> use them).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~ObjectControllersTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class ObjectControllersTests
{
    private static readonly Type[] Controllers =
    [
        typeof(ObjectsController), typeof(ObjectDownloadsController), typeof(ObjectBuildsController),
        typeof(RasterAnalysisController), typeof(StockpileController)
    ];

    private static IEnumerable<MethodInfo> Actions() => Controllers.SelectMany(c =>
        c.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Any()));

    [Test]
    public void OptionalReferenceParameters_AreNullable_SoMvcAddsNoImplicitRequired()
    {
        var nullability = new NullabilityInfoContext();
        var implicitlyRequired = Actions()
            .SelectMany(a => a.GetParameters().Select(p => (Action: a, Parameter: p)))
            .Where(x => !x.Parameter.ParameterType.IsValueType &&
                        x.Parameter.ParameterType != typeof(CancellationToken) &&
                        x.Parameter.GetCustomAttribute<RequiredAttribute>() == null &&
                        nullability.Create(x.Parameter).ReadState == NullabilityState.NotNull)
            .Select(x => $"{x.Action.DeclaringType!.Name}.{x.Action.Name}({x.Parameter.Name})")
            .ToList();

        implicitlyRequired.ShouldBeEmpty();
    }

    [Test]
    public void RouteNames_KeepTheirObjectsControllerPrefix()
    {
        var names = Actions().SelectMany(a => a.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>())
            .Select(h => h.Name).ToList();

        names.ShouldNotBeEmpty();
        names.ShouldAllBe(n => n != null && n.StartsWith("ObjectsController.", StringComparison.Ordinal));
        names.Count.ShouldBe(names.Distinct().Count());
    }
}
