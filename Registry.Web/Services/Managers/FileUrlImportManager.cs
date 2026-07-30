#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports.Import;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Import;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Default <see cref="IFileUrlImportManager"/>. Verifies a single-file URL cheaply (probe + deny-list
/// / size checks) and, on import, encrypts the optional basic-auth password before submitting the
/// <c>import-file</c> heavy task. Authorization requires Write access on the target dataset.
/// </summary>
public class FileUrlImportManager : IFileUrlImportManager
{
    private readonly IUtils _utils;
    private readonly IAuthManager _authManager;
    private readonly IHeavyTaskRunner _runner;
    private readonly IImportCredentialProtector _protector;
    private readonly GuardedHttpDownloader _downloader;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ImportSettings _settings;
    private readonly ILogger<FileUrlImportManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileUrlImportManager"/> class.
    /// </summary>
    public FileUrlImportManager(
        IUtils utils,
        IAuthManager authManager,
        IHeavyTaskRunner runner,
        IImportCredentialProtector protector,
        GuardedHttpDownloader downloader,
        IHttpContextAccessor httpContextAccessor,
        IOptions<AppSettings> appSettings,
        ILogger<FileUrlImportManager> logger)
    {
        _utils = utils;
        _authManager = authManager;
        _runner = runner;
        _protector = protector;
        _downloader = downloader;
        _httpContextAccessor = httpContextAccessor;
        _settings = appSettings.Value.Import ?? new ImportSettings();
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<UrlImportVerifyResultDto> VerifyAsync(string orgSlug, string dsSlug,
        UrlImportVerifyRequestDto request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ds = _utils.GetDataset(orgSlug, dsSlug);
        if (!await _authManager.RequestAccess(ds, AccessType.Write))
            throw new UnauthorizedException("The current user cannot import files into this dataset.");

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

    /// <inheritdoc />
    public async Task<UrlImportResultDto> ImportAsync(string orgSlug, string dsSlug,
        UrlImportRequestDto request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ds = _utils.GetDataset(orgSlug, dsSlug);
        if (!await _authManager.RequestAccess(ds, AccessType.Write))
            throw new UnauthorizedException("The current user cannot import files into this dataset.");

        var uri = FileImportPolicy.ParseHttpUrl(request.Url);

        var fileName = !string.IsNullOrWhiteSpace(request.FileName)
            ? FileImportPolicy.SanitizeFileName(request.FileName)
            : FileImportPolicy.DeriveFileName(uri);

        if (!_settings.IsExtensionAllowed(fileName))
            throw new BadRequestException($"Files of this type are not allowed: '{fileName}'.");

        var folder = NormalizeFolder(request.Folder);

        var toolParams = BuildToolParams(uri, fileName, folder, request);
        var user = await _authManager.GetCurrentUser();

        var submit = new HeavyTaskSubmitRequest(
            orgSlug, ds.Slug, "import-file", "1", fileName, toolParams, false,
            user?.Id, _httpContextAccessor.HttpContext?.User);

        var result = await _runner.SubmitAsync(submit, ct);

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
                : "ENC:" + _protector.Protect(request.Password)
        };

        if (request.SizeBytes is { } size && size > 0)
            root["sizeBytes"] = size;

        using var doc = JsonDocument.Parse(root.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static string NormalizeFolder(string? folder)
    {
        var f = (folder ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
        if (string.IsNullOrEmpty(f)) return string.Empty;

        if (f.Split('/').Any(s => s is "." or ".."))
            throw new BadRequestException("Invalid destination folder.");

        return f;
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
