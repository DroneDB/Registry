using System;
using System.Collections.Generic;
using System.Linq;
using Hangfire;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary><see cref="ILiveJobServers"/> from the Hangfire monitoring API: the servers the job store lists by heartbeat.</summary>
public sealed class HangfireLiveJobServers : ILiveJobServers
{
    private readonly JobStorage _storage;

    public HangfireLiveJobServers(JobStorage storage)
    {
        _storage = storage;
    }

    public IReadOnlySet<string> Ids() =>
        _storage.GetMonitoringApi().Servers().Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
}
