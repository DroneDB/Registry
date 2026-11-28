using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Test.Support;

/// <summary>A real <see cref="DatasetIndexQueue"/> over the datasets of a test.</summary>
public static class TestIndexQueues
{
    /// <summary>The queue, whose batches open their dataset through <paramref name="ddbManager"/>.</summary>
    /// <param name="ddbManager">The datasets of the test.</param>
    /// <param name="settings">The tuning of the queue.</param>
    /// <param name="time">Its clock; the system clock when null.</param>
    /// <param name="shutdownWait">How long its disposal waits for a running batch; the production bound when null.</param>
    public static DatasetIndexQueue Create(IDdbManager ddbManager, IndexQueueSettings settings,
        TimeProvider? time = null, TimeSpan? shutdownWait = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(ddbManager);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new DatasetIndexQueue(scopeFactory, NullLogger<DatasetIndexQueue>.Instance,
            Microsoft.Extensions.Options.Options.Create(settings), time, shutdownWait);
    }
}
