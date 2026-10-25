#nullable enable
using System;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test.Support;

/// <summary>An <see cref="IInstanceIdentity"/> with a known identity, for tests that need no storage folder.</summary>
public sealed class FixedInstanceIdentity : IInstanceIdentity
{
    public const string DefaultId = "a1b2c3d4-5e6f-4a7b-8c9d-0e1f2a3b4c5d";

    public FixedInstanceIdentity(string id = DefaultId) =>
        Current = new InstanceIdentity(id, new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc));

    public InstanceIdentity Current { get; }
}
