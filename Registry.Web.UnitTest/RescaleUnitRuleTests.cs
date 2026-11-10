using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Services.Objects;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// D-022, FR-RAS-03: the unit a rescale of a thermal formula is written in (<c>rescaleUnit</c>) is an option of libddb
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

    [Test]
    public void AUnit_IsTrimmed() => RasterOptionRules.RescaleUnit(" degC ").ShouldBe("degC");

    [TestCase(null)]
    [TestCase("")]
    [TestCase("  ")]
    public void NoUnit_IsNull(string? unit) => RasterOptionRules.RescaleUnit(unit).ShouldBeNull();

    // The names libddb's parseTemperatureUnit accepts (thermal.cpp), literally
    [TestCase("K")]
    [TestCase("k")]
    [TestCase("Kelvin")]
    [TestCase("KELVIN")]
    [TestCase("degK")]
    [TestCase("C")]
    [TestCase("celsius")]
    [TestCase("degC")]
    [TestCase("\u00b0C")]
    [TestCase("Centigrade")]
    [TestCase("F")]
    [TestCase("Fahrenheit")]
    [TestCase("degF")]
    [TestCase("\u00b0f")]
    [TestCase("deg C")]
    public void TheNamesOfLibddb_AreKnownUnits(string unit)
    {
        RasterOptionRules.IsKnownTemperatureUnit(unit).ShouldBeTrue();
        RasterOptionRules.RescaleUnit(unit).ShouldBe(unit);
    }

    [TestCase("feet")]
    [TestCase("X")]
    [TestCase("kelvins")]
    [TestCase("degree")]
    [TestCase("KC")]
    [TestCase("\u00b0")]
    public void AnotherName_IsNotAUnit_And400AtRescaleUnit(string unit)
    {
        RasterOptionRules.IsKnownTemperatureUnit(unit).ShouldBeFalse();

        var ex = Should.Throw<InvalidFieldException>(() => RasterOptionRules.RescaleUnit(unit));
        ex.Field.ShouldBe("rescaleUnit");
        ex.Message.ShouldBe($"'{unit}' is not a temperature unit: use K (Kelvin), C (Celsius) or F (Fahrenheit).");
    }
}
