using System;
using System.Linq;
using NUnit.Framework;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Managers.Wfs;
using Shouldly;

namespace Registry.Web.Test.Ogc;

/// <summary>
/// The OGC managers share six collaborators through <see cref="OgcManagerDependencies"/>: none of them lists
/// them one by one, so none is near the seven-parameter limit the constructors of this code base keep to.
/// </summary>
[TestFixture]
public class OgcManagerDependenciesTests
{
    private const int MaxConstructorParameters = 7;

    private static readonly Type[] Managers = typeof(OgcManagerBase).Assembly.GetTypes()
        .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(OgcManagerBase))).ToArray();

    [Test]
    public void EveryOgcManager_HasASmallConstructor_OverTheSharedDependencies()
    {
        Managers.Length.ShouldBeGreaterThanOrEqualTo(8);

        foreach (var manager in Managers)
        {
            var constructor = manager.GetConstructors().ShouldHaveSingleItem(manager.Name);
            var parameters = constructor.GetParameters();
            parameters.Length.ShouldBeLessThanOrEqualTo(MaxConstructorParameters, manager.Name);
            parameters.Count(p => p.ParameterType == typeof(OgcManagerDependencies)).ShouldBe(1, manager.Name);
        }
    }

    [Test]
    public void Base_RejectsMissingDependencies() =>
        Should.Throw<ArgumentNullException>(() => new WfsCapabilities(null!));
}
