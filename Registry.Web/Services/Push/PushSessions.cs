using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Builds;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Stamp = Registry.Ports.DroneDB.Stamp;

namespace Registry.Web.Services.Push;

/// <summary>The start of a push: the dataset (created on a first push), the delta against the client stamp, the session the next steps continue.</summary>
public interface IPushSessions
{
    Task<PushInitResultDto> Init(string orgSlug, string dsSlug, string checksum, StampDto stamp, CancellationToken ct = default);
}

/// <summary>Default <see cref="IPushSessions"/> (RQ-12, out of <c>PushManager</c>).</summary>
public sealed class PushSessions : IPushSessions
{
    private readonly IDatasetResolver _datasets;
    private readonly IDatasetsManager _datasetsManager;
    private readonly IDatasetAccessResolver _access;
    private readonly IDdbManager _ddbManager;
    private readonly IDdbWrapper _ddbWrapper;
    private readonly IPushSessionStore _sessionStore;
    private readonly ILogger<PushSessions> _logger;

    public PushSessions(IDatasetResolver datasets, IDatasetsManager datasetsManager, IDatasetAccessResolver access, IDdbManager ddbManager, IDdbWrapper ddbWrapper, IPushSessionStore sessionStore, ILogger<PushSessions> logger)
    {
        _datasets = datasets;
        _datasetsManager = datasetsManager;
        _access = access;
        _ddbManager = ddbManager;
        _ddbWrapper = ddbWrapper;
        _sessionStore = sessionStore;
        _logger = logger;
    }

    public async Task<PushInitResultDto> Init(string orgSlug, string dsSlug, string checksum, StampDto stamp,
        CancellationToken ct = default)
    {
        // Honoured while the target is looked up; a new dataset and the session are then written (RQ-13)
        var ds = await _datasets.GetDatasetAsync(orgSlug, dsSlug, safe: true, ct: ct);

        var validateChecksum = false;

        if (ds is null)
        {
            _logger.LogInformation("Dataset does not exist, creating it");
            await _datasetsManager.AddNew(orgSlug, new DatasetNewDto
            {
                Name = dsSlug,
                Slug = dsSlug
            }, ct);

            _logger.LogInformation("New dataset {OrgSlug}/{DsSlug} created", orgSlug, dsSlug);
            ds = await _datasets.GetDatasetAsync(orgSlug, dsSlug, ct: CancellationToken.None);

        }
        else
        {
            await _access.EnsureAccessAsync(ds, AccessType.Write);

            validateChecksum = true;
        }

        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
        Stamp ourStamp = null;

        if (validateChecksum)
        {
            if (string.IsNullOrEmpty(checksum))
                throw new BadRequestException("Checksum parameter missing (dataset exists)");

            // Is a pull required? The checksum passed by client is the checksum of the stamp
            // of the last sync. If it's different, the client should pull first.
            ourStamp = _ddbWrapper.GetStamp(ddb.DatasetFolderPath);
            if (ourStamp.Checksum != checksum)
            {
                return new PushInitResultDto
                {
                    PullRequired = true
                };
            }
        }

        ourStamp ??= _ddbWrapper.GetStamp(ddb.DatasetFolderPath);

        // Perform delta with our ddb
        var delta = _ddbWrapper.Delta(new Stamp
        {
            Checksum = stamp.Checksum,
            Entries = stamp.Entries,
            Meta = stamp.Meta
        }, ourStamp);

        // Compute locals (stops when the request goes away, before the session is written: XI-07)
        Dictionary<string, bool> locals;
        using (DdbCancellation.Enter(ct))
            locals = _ddbWrapper.ComputeDeltaLocals(delta, ddb.DatasetFolderPath);

        // Generate UUID
        var uuid = Guid.NewGuid().ToString();

        // Commit applies this delta if the dataset is still at ourStamp
        await _sessionStore.SaveAsync(PushFolders.Of(ddb, uuid), new PushSession
        {
            OurChecksum = ourStamp.Checksum,
            Delta = delta
        });

        // Return missing files list (excluding folders)
        return new PushInitResultDto
        {
            Token = uuid,
            NeededFiles =
            [
                .. delta.Adds
                    .Where(item => item.Hash.Length > 0 && !locals.ContainsKey(item.Hash))
                    .Select(item => item.Path)
            ],
            NeededMeta = [.. delta.MetaAdds],
            PullRequired = false
        };
    }
}
