using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Services.Objects;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// D-022, FR-RAS-03: the unit a rescale of a thermal formula is written in (<c>rescaleUnit</c>) is an option of libddb 2.3
/// (<c>DDBRasterOptions.rescaleUnit</c>): one rule for every endpoint that takes it, and a part of the cache key of a
/// render, so a rescale in K and the same numbers in °F are two renders.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~RescaleUnitRuleTests"
/// </summary>
[TestFixture]
public class RescaleUnitRuleTests
{
    [Test]
    public void TheCacheKey_HasTheUnitOfTheRescale_OnlyWhenItIsSet()
    {
        // The key of a render without the unit is the one it always was
        new RasterVisualization(Formula: "CELSIUS", Colormap: "ironbow", Rescale: "280,310").CacheKey
            .ShouldBe("||CELSIUS||ironbow|280,310|");
        new RasterVisualization(Formula: "CELSIUS", Colormap: "ironbow", Rescale: "280,310", RescaleUnit: " K ").CacheKey
            .ShouldBe("||CELSIUS||ironbow|280,310||K");
        new RasterVisualization(Formula: "CELSIUS", Colormap: "ironbow", Rescale: "280,310", Nodata: -9999, RescaleUnit: "F")
            .CacheKey.ShouldBe("||CELSIUS||ironbow|280,310|-9999|F");
    }

    [Test]
    public void TheOptionsOfARender_HoldTheNodataValueAndTheUnit_OrAreNull()
    {
        new RasterVisualization(Formula: "CELSIUS").Options.ShouldBeNull();
        new RasterVisualization(RescaleUnit: "  ").Options.ShouldBeNull();

        var options = new RasterVisualization(Formula: "CELSIUS", Nodata: 0, RescaleUnit: "K").Options;
        options.ShouldNotBeNull();
        options.Nodata.ShouldBe(0);
        options.RescaleUnit.ShouldBe("K");
        options.Formula.ShouldBeNull("the formula of a render is its argument, not an option");
        options.Bbox.ShouldBeNull();
    }

    [TestCase(3)]
    [TestCase(4)]
    public void AUnit_OnLibddb23OrLater_IsTrimmed(int abiMinor) =>
        RasterOptionRules.RescaleUnit(" degC ", new AbiMinorRuntime(abiMinor).Capabilities).ShouldBe("degC");

    [TestCase(null)]
    [TestCase("")]
    [TestCase("  ")]
    public void NoUnit_IsNull_OnAnyLibddb(string? unit) =>
        RasterOptionRules.RescaleUnit(unit, new AbiMinorRuntime(2).Capabilities).ShouldBeNull();

    [Test]
    public void AUnit_OnLibddb22_Is400AtRescaleUnit_WithTheReason()
    {
        var ex = Should.Throw<InvalidFieldException>(() =>
            RasterOptionRules.RescaleUnit("K", new AbiMinorRuntime(2).Capabilities));

        ex.Field.ShouldBe("rescaleUnit");
        ex.Message.ShouldStartWith("A rescale in another temperature unit needs libddb 2.3 or later: this server runs libddb ");
        ex.Message.ShouldContain("(ABI 2.2)");
    }
}
