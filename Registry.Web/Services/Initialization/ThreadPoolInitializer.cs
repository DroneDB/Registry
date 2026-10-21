#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Hosting;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Services.Initialization;

/// <summary>
/// Startup task of the web host (<see cref="StartupTaskOrder.ThreadPool"/>): raises the thread pool's
/// minimum worker threads to <see cref="HostResourceOptions.WorkerThreads"/> when it is set (it was a side
/// effect of <c>ConfigureServices</c>).
/// </summary>
internal sealed class ThreadPoolInitializer(IOptions<HostResourceOptions> options, ILogger<ThreadPoolInitializer> logger)
    : IStartupTask
{
    public int Order => StartupTaskOrder.ThreadPool;

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var workerThreads = options.Value.WorkerThreads;
        if (workerThreads > 0)
        {
            ThreadPool.GetMinThreads(out _, out var ioCompletionThreads);
            ThreadPool.SetMinThreads(workerThreads, ioCompletionThreads);
            logger.LogInformation("Thread pool minimum worker threads: {WorkerThreads}", workerThreads);
        }

        return Task.CompletedTask;
    }
}
