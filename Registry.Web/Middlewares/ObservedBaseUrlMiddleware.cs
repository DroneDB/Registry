using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Storage;
using Registry.Web.Services.UpdateCheck;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Middlewares;

/// <summary>
/// Remembers the public URL the deployment is reached on (<see cref="ObservedBaseUrlStore"/>), for the update check that
/// runs on a node that serves no request. The URL is the one <see cref="IUrlBuilder"/> gives, so there is one rule for it.
/// Not needed, and not written, when an external URL is configured: that wins.
/// </summary>
/// <remarks>
/// Only the request of an administrator records it (<see cref="AdminAuthorizationHandler.IsAdminAsync"/>: the token and the
/// identity store agree), once the pipeline after this middleware (authentication included) has run: the Host header of a
/// request is the client's own, and nobody but the people who run the deployment may choose the URL the update check
/// reports and its licence link points to; any user can sign up or be added, and sends the Host they like.
/// </remarks>
public sealed class ObservedBaseUrlMiddleware : IMiddleware
{
    private readonly IUrlBuilder _urls;
    private readonly IUserAuthSnapshotProvider _snapshots;
    private readonly ObservedBaseUrlStore _store;
    private readonly HttpHostOptions _http;
    private readonly ILogger<ObservedBaseUrlMiddleware> _logger;

    public ObservedBaseUrlMiddleware(IUrlBuilder urls, IUserAuthSnapshotProvider snapshots, ObservedBaseUrlStore store,
        IOptions<HttpHostOptions> http, ILogger<ObservedBaseUrlMiddleware> logger)
    {
        _urls = urls;
        _snapshots = snapshots;
        _store = store;
        _http = http.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        await next(context);

        if (string.IsNullOrWhiteSpace(_http.ExternalUrlOverride) &&
            await AdminAuthorizationHandler.IsAdminAsync(context.User, _snapshots))
        {
            try
            {
                _store.Observe(_urls.GetLocalHost());
            }
            catch (StorageDocumentException ex)
            {
                // A request never fails because of this
                _logger.LogDebug(ex, "Cannot record the public URL of the deployment");
            }
        }
    }
}
