using System;
using System.Collections.Generic;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test.Support;

/// <summary>The live job servers of a test: the ids it adds to <see cref="Live"/> (none at first).</summary>
public sealed class FakeLiveJobServers : ILiveJobServers
{
    /// <summary>The servers that are alive now.</summary>
    public HashSet<string> Live { get; } = new(StringComparer.Ordinal);

    public IReadOnlySet<string> Ids() => Live;
}
