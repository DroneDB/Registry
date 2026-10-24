#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Uploads;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Default <see cref="IObjectsManager"/>: the entry operations other features use, over
/// <see cref="IEntryCommandService"/> (folders, deletes) and the upload pipeline (<see cref="UploadManager"/>).
/// </summary>
public class ObjectsManager : IObjectsManager
{
    private readonly IEntryCommandService _commands;
    private readonly UploadManager _uploads;
    private readonly IDatasetResolver _datasets;
    private readonly IDdbManager _ddbManager;
    private readonly ScratchFileProvider _scratch;
    private readonly IFileSystem _fs;
    private readonly ILogger<ObjectsManager> _logger;

    public ObjectsManager(IEntryCommandService commands, UploadManager uploads, IDatasetResolver datasets, IDdbManager ddbManager,
        ScratchFileProvider scratch, IFileSystem fs, ILogger<ObjectsManager> logger)
    {
        _commands = commands;
        _uploads = uploads;
        _datasets = datasets;
        _ddbManager = ddbManager;
        _scratch = scratch;
        _fs = fs;
        _logger = logger;
    }

    public async Task<EntryDto> AddNew(string orgSlug, string dsSlug, string path, Stream? stream = null)
    {
        // A folder has no payload to stage: validated, created and indexed at once
        if (stream == null)
            return await _commands.CreateFolderAsync(orgSlug, dsSlug, path);

        // A file: the pipeline of streamed uploads (validation, atomic move, index, quarantine on index
        // failure, quota on the authoritative on-disk size)
        using var ctx = await _uploads.BeginAsync(orgSlug, dsSlug);
        var file = await _uploads.StreamAsync(ctx, stream);
        return await _uploads.CommitAsync(ctx, path, file);
    }

    /// <summary>
    /// Streams a forward-only upload body to a temporary file located on the same storage
    /// volume as the dataset, so it can later be moved (not copied) into its final place,
    /// hashing it on the way.
    /// </summary>
    public async Task<StreamedFile> StreamToTempAsync(string orgSlug, string dsSlug, Stream source)
    {
        // No access check here: the commit checks write access and deletes this file when refused
        var ds = await _datasets.GetDatasetAsync(orgSlug, dsSlug);
        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

        return await DatasetUploads.StreamToTempAsync(_scratch, _fs, ddb.DatasetFolderPath, source);
    }

    /// <summary>
    /// Finalizes a streamed upload: validates access and quota, moves the already-written
    /// temporary file into its final dataset location and indexes it.
    /// </summary>
    public async Task<EntryDto> CommitStreamedAsync(string orgSlug, string dsSlug, string path, StreamedFile file)
    {
        UploadRequestContext ctx;
        try
        {
            ctx = await _uploads.BeginAsync(orgSlug, dsSlug);
        }
        catch
        {
            // Refused before the commit (access, dataset): drop our own temp file, never a foreign one
            await DeleteStreamedTempAsync(orgSlug, dsSlug, file.TempPath);
            throw;
        }

        using (ctx)
            return await _uploads.CommitAsync(ctx, path, file);
    }

    private async Task DeleteStreamedTempAsync(string orgSlug, string dsSlug, string? tempFilePath)
    {
        try
        {
            var ds = await _datasets.GetDatasetAsync(orgSlug, dsSlug, safe: true);
            if (ds == null || string.IsNullOrWhiteSpace(tempFilePath)) return;
            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            if (UploadFiles.IsInUploadsFolder(ddb, tempFilePath))
                _fs.TryDelete(tempFilePath, ex => _logger.LogDebug(ex, "Cannot delete the temp upload '{TempFile}'", tempFilePath));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cannot delete the temp upload '{TempFile}'", tempFilePath);
        }
    }
}
