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

/// <summary>The end of a push: the delta applied if the dataset has not changed since its start, then the builds of the pushed files.</summary>
public interface IPushCommit
{
    Task Commit(string orgSlug, string dsSlug, string token, CancellationToken ct = default);
}

/// <summary>Default <see cref="IPushCommit"/> (RQ-12, out of <c>PushManager</c>).</summary>
public sealed class PushCommit : IPushCommit
{
    private readonly IDatasetAccessResolver _access;
    private readonly IPushSessionStore _sessionStore;
    private readonly IDdbWrapper _ddbWrapper;
    private readonly IFileSystem _fileSystem;
    private readonly ImportSettings _importSettings;
    private readonly IBuildScheduler _builds;

    public PushCommit(IDatasetAccessResolver access, IPushSessionStore sessionStore, IDdbWrapper ddbWrapper, IFileSystem fileSystem, IOptions<ImportSettings> importSettings, IBuildScheduler builds)
    {
        _access = access;
        _sessionStore = sessionStore;
        _ddbWrapper = ddbWrapper;
        _fileSystem = fileSystem;
        _importSettings = importSettings.Value;
        _builds = builds;
    }

    public async Task Commit(string orgSlug, string dsSlug, string token, CancellationToken ct = default)
    {
        // Honoured while the dataset is resolved, and by the two native calls that read the pushed files below (XI-07)
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);

        var ddb = access.Ddb;

        var baseTempFolder = PushFolders.Of(ddb, token);
        var addTempFolder = PushFolders.Adds(baseTempFolder);
        var metaFile = PushFolders.Meta(baseTempFolder);

        var session = await _sessionStore.LoadAsync(baseTempFolder) ??
                      throw new ConflictException("Push session not found, please push again");

        // Check that the dataset has not changed since Init! If it has, another client
        // might have performed changes that could conflict with our operation
        // TODO: we could check for conflicts rather than failing and continue
        // the operation if no conflicts are detected.
        if (_ddbWrapper.GetStampChecksum(ddb.DatasetFolderPath) != session.OurChecksum)
        {
            throw new ConflictException("The dataset has been changed by another user while pushing. Please try again!");
        }

        // Unchanged dataset: the delta computed by Init still holds
        var delta = session.Delta;

        // The uploads checked against the delta when they arrived (XI-05), taken before the local
        // files are linked in: libddb hashes the linked ones, and nothing it was not told about
        var verifiedHashes = await _sessionStore.VerifiedHashesAsync(baseTempFolder, addTempFolder, session);

        // Create hard links for local files: stops, nothing applied, when the request goes away
        using (DdbCancellation.Enter(ct))
            _ddbWrapper.ComputeDeltaLocals(delta, ddb.DatasetFolderPath, addTempFolder);

        foreach (var add in delta.Adds.Where(item => item.Hash.Length > 0))
            if (!_fileSystem.Exists(Path.Combine(addTempFolder, add.Path)))
                throw new BadRequestException($"Cannot commit: missing '{add.Path}'");

        // Extension policy gate - check all files being committed
        var blockedFiles = delta.Adds
            .Where(add => !_importSettings.IsExtensionAllowed(add.Path))
            .Select(add => add.Path)
            .ToArray();

        if (blockedFiles.Length > 0)
            throw new ExtensionBlockedException(blockedFiles, _importSettings);

        // Read meta dump
        string metaDump = null;
        if (_fileSystem.Exists(metaFile))
        {
            metaDump = await _fileSystem.ReadAllTextAsync(metaFile, ct);
        }

        // Applies delta. Atomic to a cancel (libddb, XI-07): a request that goes away while it reads and hashes the
        // added files stops it with the dataset and the pushed files as they were (DdbCanceledException, the session
        // stays: the client commits again); once it starts to change the dataset it finishes whole, returns normally
        // and the commit goes on whatever the token says now, the builds below queueing with no token
        var target = ddb.DatasetFolderPath;
        List<string> conflicts;
        using (DdbCancellation.Enter(ct))
            conflicts = _ddbWrapper.ApplyDelta(delta, addTempFolder, target, MergeStrategy.KeepTheirs, metaDump, verifiedHashes);

        if (conflicts.Count > 0)
        {
            // This should never happen, since we merge conflicts using keep theirs
            throw new ConflictException("Merge conflicts detected, try pulling first.");
        }

        // Delete temp folder
        _fileSystem.FolderDelete(baseTempFolder);

        // Build the pushed files libddb reports buildable, then what their arrival unblocked
        var dataset = BuildTarget.Of(access, orgSlug, dsSlug);
        await _builds.QueueBuildableAsync(dataset, [.. delta.Adds.Where(item => item.Hash.Length > 0).Select(item => item.Path)], ct: CancellationToken.None);
        await _builds.QueuePendingAsync(dataset, ct: CancellationToken.None);
    }
}
