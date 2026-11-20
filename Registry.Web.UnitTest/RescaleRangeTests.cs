using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Services.Objects;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-RAS-10: the <c>rescale</c> parameter is <c>min,max</c>, two finite numbers with '.' as the decimal sign and min less
/// than max; anything else is a 400 at the <c>rescale</c> member, not a failure of the native render.
/// </summary>
[TestFixture]
public class RescaleRangeTests
{
    [TestCase("0,255", 0d, 255d)]
    [TestCase("-1.5,2.5", -1.5, 2.5)]
    [TestCase(" 0 , 1e3 ", 0d, 1000d)]
    [TestCase("0.371,0.947", 0.371, 0.947)]
    [TestCase("-3.4e38,3.4e38", -3.4e38, 3.4e38)]
    public void TryParse_ReadsTwoNumbers(string value, double min, double max)
    {
        RescaleRange.TryParse(value, out var actualMin, out var actualMax).ShouldBeTrue();
        (actualMin, actualMax).ShouldBe((min, max));
    }

    [TestCase("abc")]
    [TestCase("1")]
    [TestCase("1,")]
    [TestCase(",5")]
    [TestCase("1,2,3")]
    [TestCase("40,10")]
    [TestCase("5,5")]
    [TestCase("NaN,5")]
    [TestCase("1,Infinity")]
    [TestCase("12,5")]
    [TestCase("1;5")]
    [TestCase("0,1e39")]
    [TestCase("-1e39,0")]
    public void TryParse_RefusesAnythingElse(string value) => RescaleRange.TryParse(value, out _, out _).ShouldBeFalse();

    [Test]
    public void Validated_LetsTheDefaultThrough_AndRefusesABadValueAtTheRescaleMember()
    {
        RescaleRange.Validated(null).ShouldBeNull();
        // Blank is the default range: libddb would refuse the spaces (std::stof)
        RescaleRange.Validated("").ShouldBeNull();
        RescaleRange.Validated("  ").ShouldBeNull();
        new RasterVisualization(Rescale: "  ").Rescale.ShouldBeNull();
        RescaleRange.Validated("0,1").ShouldBe("0,1");

        var ex = Should.Throw<InvalidFieldException>(() => RescaleRange.Validated("abc"));
        ex.Field.ShouldBe("rescale");
        ex.FieldErrors!["rescale"].ShouldBe([RescaleRange.InvalidMessage("abc")]);
        ex.Message.ShouldBe("The rescale value 'abc' is invalid: use two numbers, min and max, separated by a comma, with min less than max (for example 0,255)");

        // libddb reads each number as a float: beyond float.MaxValue it would fail
        Should.Throw<InvalidFieldException>(() => RescaleRange.Validated("0,1e39")).Field.ShouldBe("rescale");
    }

    [Test]
    public void RasterVisualization_RefusesABadRescaleWhenItIsBuilt()
    {
        new RasterVisualization(Rescale: "0,10").Rescale.ShouldBe("0,10");
        new RasterVisualization().Rescale.ShouldBeNull();
        Should.Throw<InvalidFieldException>(() => new RasterVisualization(Colormap: "viridis", Rescale: "10,0"));
    }
}
