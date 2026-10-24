#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Uploads;

/// <summary>The index step of an upload (RQ-12, out of <c>UploadManager</c>): the files of a request become entries in one batch.</summary>
public sealed class UploadIndexer
{
    private readonly ILogger<UploadIndexer> _logger;
    private readonly IDatasetIndexQueue? _indexQueue;

    public UploadIndexer(ILogger<UploadIndexer> logger, IDatasetIndexQueue? indexQueue = null)
    {
        _logger = logger;
        _indexQueue = indexQueue;
    }

    /// <summary>Registers the upload of <paramref name="ctx"/> with the index lane, or null when there is no lane.</summary>
    public UploadTicket? BeginUpload(UploadRequestContext ctx) => _indexQueue?.BeginUpload(ctx.Key);

    /// <summary>One index batch for every file of the request; each file gets its own entry or error.</summary>
    public async Task IndexAsync(UploadRequestContext ctx, IReadOnlyList<MovedUploadPart> parts)
    {
        if (parts.Count == 0)
        {
            ctx.Ticket?.End();
            return;
        }

        _logger.LogInformation("Files saved, adding {Count} to DDB", parts.Count);

        if (_indexQueue != null)
        {
            // Coalesces with other concurrent uploads to the same dataset into one native batch and
            // one write transaction; the hashes computed while streaming save reading the files again.
            var outcomes = await _indexQueue.EnqueueAsync(ctx.Key,
                parts.Select(p => new AddItem(p.Part.Path, p.Part.File.Hash)).ToArray(), ctx.Ticket);
            for (var i = 0; i < parts.Count; i++)
            {
                parts[i].Entry = outcomes[i].Entry;
                parts[i].Error = outcomes[i].Error ??
                                 (outcomes[i].Entry == null ? new InvalidOperationException("Cannot find just added file!") : null);
            }

            return;
        }

        // Fallback for callers/tests that construct the pipeline without DI wiring
        foreach (var part in parts)
        {
            try
            {
                ctx.Ddb.AddRaw(part.Part.Path);
                part.Entry = ctx.Ddb.GetEntry(part.Part.Path) ??
                             throw new DdbException("Cannot find just added file!");
            }
            catch (Exception ex)
            {
                part.Error = ex;
            }
        }
    }
}
