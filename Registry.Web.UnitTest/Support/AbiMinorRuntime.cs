using System.Text.Json.Nodes;
using Registry.Adapters.Testing;
using Registry.Ports.DroneDB;

namespace Registry.Web.Test.Support;

/// <summary>
/// A <see cref="FakeDdbRuntime"/> whose libddb reports another ABI minor: the recorded capabilities (libddb 2.2) with
/// <c>abi.minor</c> replaced, for what Registry gates on the ABI (the window of a raster export needs 2.3).
/// </summary>
public sealed class AbiMinorRuntime(int minor) : FakeDdbRuntime
{
    public override DdbCapabilities Capabilities { get; } = WithMinor(minor);

    private static DdbCapabilities WithMinor(int minor)
    {
        var json = JsonNode.Parse(ContractFixture.Load("capabilities").Output.ToJsonString())!;
        json["abi"]!["minor"] = minor;
        return DdbCapabilities.Parse(json.ToJsonString());
    }
}
