#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.Import;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Web-layer orchestration for importing a single file from a URL into an existing dataset. Verifies
/// the URL cheaply (probe) and, on import, encrypts the optional basic-auth credential and submits the
/// <c>import-file</c> heavy task.
/// </summary>
public class FileUrlImportManager
{
    private readonly IDatasetAccessResolver _access;
    private readonly CurrentUserTaskSubmitter _submitter;
    private readonly ImportCredentials _credentials;
    private readonly GuardedHttpDownloader _downloader;
    private readonly ImportSettings _settings;
    private readonly DatasetPathPolicy _paths;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileUrlImportManager"/> class.
    /// </summary>
    public FileUrlImportManager(
        IDatasetAccessResolver access,
        CurrentUserTaskSubmitter submitter,
        ImportCredentials credentials,
        GuardedHttpDownloader downloader,
        IOptions<ImportSettings> importSettings,
        DatasetPathPolicy paths)
    {
        _access = access;
        _submitter = submitter;
        _credentials = credentials;
        _downloader = downloader;
        _settings = importSettings.Value;
        _paths = paths;
    }

    /// <summary>
    /// Probes the URL without transferring the body and reports reachability, size, the derived file
    /// name and the outcome of the deny-list / size checks.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="request">The verify request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The verification result.</returns>
    public async Task<UrlImportVerifyResultDto> VerifyAsync(string orgSlug, string dsSlug,
        UrlImportVerifyRequestDto request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);

        var uri = FileImportPolicy.ParseHttpUrl(request.Url);
        var probe = await _downloader.ProbeAsync(uri, request.Username, request.Password, ct);

        var fileName = FileImportPolicy.DeriveFileName(uri, probe.SuggestedFileName);
        var blocked = !_settings.IsExtensionAllowed(fileName);

        var cap = _settings.EffectiveFileImportCapBytes();
        var sizeExceeds = cap > 0 && probe.SizeBytes is > 0 && probe.SizeBytes.Value > cap;

        return new UrlImportVerifyResultDto
        {
            Reachable = probe.Reachable,
            SizeBytes = probe.SizeBytes,
            FileName = fileName,
            Blocked = blocked,
            SizeExceedsLimit = sizeExceeds,
            Note = BuildNote(probe, blocked, sizeExceeds, cap)
        };
    }

    /// <summary>
    /// Submits the <c>import-file</c> heavy task after re-validating the request and encrypting the
    /// optional basic-auth password.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <param name="request">The import request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The submitted task id.</returns>
    public async Task<UrlImportResultDto> ImportAsync(string orgSlug, string dsSlug,
        UrlImportRequestDto request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);
        var ds = access.Dataset;

        var uri = FileImportPolicy.ParseHttpUrl(request.Url);

        var fileName = !string.IsNullOrWhiteSpace(request.FileName)
            ? FileImportPolicy.SanitizeFileName(request.FileName)
            : FileImportPolicy.DeriveFileName(uri);

        if (!_settings.IsExtensionAllowed(fileName))
            throw new BadRequestException($"Files of this type are not allowed: '{fileName}'.");

        var folder = _paths.NormalizeFolder(request.Folder);

        var toolParams = BuildToolParams(uri, fileName, folder, request);

        var result = await _submitter.SubmitAsync(orgSlug, ds.Slug, "import-file", "1", fileName, toolParams,
            force: false, ct);

        return new UrlImportResultDto { TaskId = result.TaskId };
    }

    // Serializes the import-file tool params, encrypting the password so it is never persisted in
    // plaintext in the Hangfire job payload / job index.
    private JsonElement BuildToolParams(Uri uri, string fileName, string folder, UrlImportRequestDto request)
    {
        var root = new JsonObject
        {
            ["url"] = uri.ToString(),
            ["fileName"] = fileName,
            ["folder"] = folder,
            ["overwrite"] = request.Overwrite,
            ["username"] = request.Username ?? string.Empty,
            ["password"] = string.IsNullOrEmpty(request.Password)
                ? string.Empty
                : _credentials.Seal(request.Password)
        };

        if (request.SizeBytes is { } size && size > 0)
            root["sizeBytes"] = size;

        return root.ToElement();
    }

    private static string? BuildNote(UrlProbeResult probe, bool blocked, bool sizeExceeds, long cap)
    {
        if (!probe.Reachable) return probe.Message;
        if (blocked) return "This file type cannot be imported.";
        if (sizeExceeds)
            return $"The file exceeds the maximum allowed size ({CommonUtils.GetBytesReadable(cap)}).";
        return probe.SizeBytes is null
            ? "The server did not report a size; it will be enforced during the download."
            : null;
    }
}
