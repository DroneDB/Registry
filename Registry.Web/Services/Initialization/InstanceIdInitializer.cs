#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Hosting;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using Registry.Web.Services.UpdateCheck;

namespace Registry.Web.Services.Initialization;

/// <summary>
/// Startup task of every host with the job storage (<see cref="StartupTaskOrder.InstanceIdentity"/>): creates or loads
/// the instance identity, before the recurring job definitions that derive the time of the update check from it, and
/// tells the administrator what the update check sends (or that it is off), at every start.
/// </summary>
internal sealed class InstanceIdInitializer : IStartupTask
{
    private readonly IInstanceIdentity _identity;
    private readonly UpdateCheckOptions _options;
    private readonly ILogger _logger;

    public InstanceIdInitializer(IInstanceIdentity identity, IOptions<UpdateCheckOptions> options,
        ILoggerFactory loggerFactory)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        // The start-up category is always written, whatever the configured level: an administrator must see it
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger(RegistryHost.StartupLogCategory);
    }

    public int Order => StartupTaskOrder.InstanceIdentity;

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var identity = _identity.Current;

        if (_options.Enabled)
            _logger.LogInformation("{Disclosure}", UpdateCheckDisclosure.For(identity.Id));
        else
            _logger.LogInformation(UpdateCheckDisclosure.DisabledText);

        return Task.CompletedTask;
    }
}
