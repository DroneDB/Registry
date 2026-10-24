#nullable enable
using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using NUnit.Framework;
using Registry.Test.Common;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-14: what the API description may rely on. Every action says what it answers (on the action or on its
/// controller: the OGC controllers declare their XML errors once), and an action that answers one DTO is declared
/// <c>ActionResult&lt;T&gt;</c>, so the body type is checked by the compiler and read by the API description.
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public sealed class Rq14ApiContractTests
{
    private static readonly Type[] Controllers = typeof(Program).Assembly.GetExportedTypes()
        .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract).ToArray();

    private static MethodInfo[] Actions() => Controllers
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        .Where(m => m.GetCustomAttributes().Any(a => a is HttpMethodAttribute) &&
                    m.GetCustomAttribute<NonActionAttribute>() == null)
        .ToArray();

    [Test]
    public void EveryAction_DeclaresWhatItAnswers()
    {
        Actions().Length.ShouldBeGreaterThan(150);

        Actions()
            .Where(a => !a.GetCustomAttributes<ProducesResponseTypeAttribute>().Any() &&
                        !a.DeclaringType!.GetCustomAttributes<ProducesResponseTypeAttribute>().Any())
            .Select(a => $"{a.DeclaringType!.Name}.{a.Name}")
            .ShouldBeEmpty("an action without [ProducesResponseType] leaves the API description empty");
    }

    [Test]
    public void AnAction_ThatAnswersADto_DeclaresActionResultOfIt()
    {
        // The JSON actions of the controllers the Hub reads: none returns the untyped IActionResult for a body
        // that is one DTO (files, XML, redirects and empty answers stay IActionResult)
        var typed = Actions().Count(a => IsActionResultOfT(a.ReturnType));

        typed.ShouldBeGreaterThanOrEqualTo(101, "ActionResult<T> actions only grow: a JSON action that goes back to IActionResult needs a reason");
    }

    private static bool IsActionResultOfT(Type returnType)
    {
        var type = returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(System.Threading.Tasks.Task<>)
            ? returnType.GetGenericArguments()[0]
            : returnType;
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ActionResult<>);
    }
}
