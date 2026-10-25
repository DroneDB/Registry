#nullable enable
using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.UpdateCheck;

/// <summary>
/// <see cref="IInstanceIdentity"/> kept in <c>.instance-id</c> of the storage folder. The first node to start creates the
/// file, exclusively; the others load the winner's, so a deployment never has two identities. A file that cannot be
/// read (damaged, not a UUID) is replaced by a new identity, like a deleted one.
/// </summary>
public sealed class InstanceIdentityStore : IInstanceIdentity
{
    private readonly StorageDocument<InstanceIdentity> _file;
    private readonly TimeProvider _time;
    private readonly ILogger<InstanceIdentityStore> _logger;
    private readonly Lock _gate = new();
    private InstanceIdentity? _current;

    public InstanceIdentityStore(IOptions<StorageOptions> storage, TimeProvider time, ILogger<InstanceIdentityStore> logger)
    {
        _file = new StorageDocument<InstanceIdentity>(storage, MagicStrings.InstanceIdFileName, logger);
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public InstanceIdentity Current
    {
        get
        {
            lock (_gate)
                return _current ??= LoadOrCreate();
        }
    }

    private InstanceIdentity LoadOrCreate()
    {
        var created = new InstanceIdentity(Guid.NewGuid().ToString(), _time.GetUtcNow().UtcDateTime);

        // Of several nodes that start together on a new deployment, the first one's identity is everyone's
        if (Valid(_file.CreateOrRead(created)) is { } identity)
        {
            if (identity == created)
                _logger.LogInformation("Created the instance identity {InstanceId}", identity.Id);
            return identity;
        }

        // The file exists but holds no identity: replace it
        _logger.LogWarning("The instance identity file is not readable: creating a new identity");
        _file.Write(created);
        return created;
    }

    private static InstanceIdentity? Valid(InstanceIdentity? identity) =>
        identity is not null && Guid.TryParse(identity.Id, out _) ? identity with { InstalledAt = AsUtc(identity.InstalledAt) } : null;

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
