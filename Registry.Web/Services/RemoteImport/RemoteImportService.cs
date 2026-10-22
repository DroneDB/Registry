#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.Import;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Import;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.RemoteImport;

/// <summary>The administrator's side of the remote import: queue one, follow it, cancel it.</summary>
public interface IRemoteImportService
{
    /// <summary>Validates the request and queues the import of one dataset.</summary>
    Task<RemoteImportJobDto> StartAsync(ImportDatasetRequestDto request, CancellationToken ct);

    /// <summary>Validates the request and queues the import of every dataset of an organization.</summary>
    Task<RemoteImportJobDto> StartAsync(ImportOrganizationRequestDto request, CancellationToken ct);

    /// <summary>The state of the import job <paramref name="jobId"/>; <see cref="NotFoundException"/> if it is not one.</summary>
    RemoteImportStatusDto GetStatus(string jobId);

    /// <summary>Cancels the import job <paramref name="jobId"/>; <see cref="NotFoundException"/> if it is not one.</summary>
    void Cancel(string jobId);
}

/// <summary>Default <see cref="IRemoteImportService"/>: the import itself runs in <see cref="RemoteImportJob"/>.</summary>
public sealed class RemoteImportService : IRemoteImportService
{
    private readonly SsrfGuard _ssrfGuard;
    private readonly IImportCredentialProtector _credentials;
    private readonly IBackgroundJobsProcessor _jobs;
    private readonly IJobOutcomeReader _outcomes;
    private readonly IUrlBuilder _urls;

    public RemoteImportService(SsrfGuard ssrfGuard, IImportCredentialProtector credentials,
        IBackgroundJobsProcessor jobs, IJobOutcomeReader outcomes, IUrlBuilder urls)
    {
        _ssrfGuard = ssrfGuard;
        _credentials = credentials;
        _jobs = jobs;
        _outcomes = outcomes;
        _urls = urls;
    }

    public async Task<RemoteImportJobDto> StartAsync(ImportDatasetRequestDto request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureSourceAsync(request.SourceRegistryUrl, request.SourceOrganization, ct);
        if (string.IsNullOrWhiteSpace(request.SourceDataset))
            throw new BadRequestException("SourceDataset is required");

        return Queue(new RemoteImportRequest(request.SourceRegistryUrl, request.Username, Protect(request.Password),
            request.SourceOrganization, request.SourceDataset,
            request.DestinationOrganization ?? request.SourceOrganization, request.DestinationDataset, request.Mode));
    }

    public async Task<RemoteImportJobDto> StartAsync(ImportOrganizationRequestDto request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureSourceAsync(request.SourceRegistryUrl, request.SourceOrganization, ct);

        return Queue(new RemoteImportRequest(request.SourceRegistryUrl, request.Username, Protect(request.Password),
            request.SourceOrganization, null, request.DestinationOrganization ?? request.SourceOrganization, null,
            request.Mode));
    }

    public RemoteImportStatusDto GetStatus(string jobId)
    {
        var outcome = Find(jobId);
        return new RemoteImportStatusDto
        {
            JobId = outcome.JobId,
            State = outcome.State,
            Result = outcome.Result,
            Error = outcome.Error
        };
    }

    public void Cancel(string jobId)
    {
        Find(jobId);
        _jobs.Delete(jobId);
    }

    private JobOutcome<ImportResultDto> Find(string jobId) =>
        (string.IsNullOrWhiteSpace(jobId) ? null : _outcomes.Find<RemoteImportJob, ImportResultDto>(jobId))
        ?? throw new NotFoundException($"Import job '{jobId}' not found");

    private RemoteImportJobDto Queue(RemoteImportRequest request)
    {
        if (!Enum.IsDefined(request.Mode))
            throw new BadRequestException($"Unknown import mode {request.Mode}");

        var jobId = _jobs.Enqueue<RemoteImportJob>(job => job.RunAsync(request, CancellationToken.None));
        return new RemoteImportJobDto
        {
            JobId = jobId,
            StatusUrl = $"{_urls.GetLocalHost()}/{RoutesHelper.SystemRadix}/imports/{Uri.EscapeDataString(jobId)}"
        };
    }

    private string? Protect(string? password) =>
        string.IsNullOrWhiteSpace(password) ? null : _credentials.Protect(password);

    // The source Registry: an http(s) scheme and host only, not a private or reserved address (the SSRF guard),
    // and an organization
    private async Task EnsureSourceAsync(string? registryUrl, string? organization, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(registryUrl))
            throw new BadRequestException("SourceRegistryUrl is required");

        if (!Uri.TryCreate(registryUrl, UriKind.Absolute, out var uri))
            throw new BadRequestException($"SourceRegistryUrl is not a valid URL: {registryUrl}");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new BadRequestException($"SourceRegistryUrl must use http or https scheme: {registryUrl}");
        if (!string.IsNullOrEmpty(uri.PathAndQuery) && uri.PathAndQuery != "/")
            throw new BadRequestException("SourceRegistryUrl must not contain path or query string, only scheme " +
                                          $"and host (e.g., https://hub.dronedb.app): {registryUrl}");
        if (!string.IsNullOrEmpty(uri.Fragment))
            throw new BadRequestException($"SourceRegistryUrl must not contain fragment: {registryUrl}");

        await _ssrfGuard.AssertAllowedAsync(uri.Host, ct);

        if (string.IsNullOrWhiteSpace(organization))
            throw new BadRequestException("SourceOrganization is required");
    }
}
