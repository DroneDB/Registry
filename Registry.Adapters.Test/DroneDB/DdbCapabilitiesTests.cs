#nullable enable
using System;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Shouldly;

namespace Registry.Adapters.Test.DroneDB;

/// <summary>XI-01: <c>DDBGetCapabilities()</c> parsed into <see cref="DdbCapabilities"/> (fields are only ever added).</summary>
[TestFixture]
public class DdbCapabilitiesTests
{
    private const string Json = """
        {
          "abi": { "major": 1, "minor": 2 },
          "version": "2.1.0 (git commit abc)",
          "errorCodes": { "NONE": 0, "EXCEPTION": 1, "INVALIDARGS": 7 },
          "errorReasons": [ "lowDiskSpace" ],
          "cancellable": [ "DDBBuild" ],
          "symbols": [ "DDBBuild", "DDBGetAbiVersion" ],
          "aFieldOfALaterAbi": { "x": 1 }
        }
        """;

    [Test]
    public void Parse_ReadsEveryField_AndIgnoresUnknownOnes()
    {
        var capabilities = DdbCapabilities.Parse(Json);

        (capabilities.AbiMajor, capabilities.AbiMinor, capabilities.Version).ShouldBe((1, 2, "2.1.0 (git commit abc)"));
        capabilities.ErrorCodes["INVALIDARGS"].ShouldBe(7);
        capabilities.ErrorReasons.ShouldBe(["lowDiskSpace"]);
        capabilities.IsCancellable("DDBBuild").ShouldBeTrue();
        capabilities.IsCancellable("DDBAdd").ShouldBeFalse();
        capabilities.Exports("DDBGetAbiVersion").ShouldBeTrue();
        capabilities.Exports("DDBAdd").ShouldBeFalse();
    }

    [TestCase("""{"version": "x", "errorCodes": {}, "errorReasons": [], "cancellable": [], "symbols": []}""")]
    [TestCase("""{"abi": {"major": 1, "minor": 1}, "version": "x", "errorCodes": {}, "errorReasons": [], "cancellable": []}""")]
    [TestCase("[]")]
    [TestCase("not json")]
    public void Parse_WithoutAnAbi11Field_IsAFormatError(string json)
    {
        Should.Throw<FormatException>(() => DdbCapabilities.Parse(json));
    }
}
