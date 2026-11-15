using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Storage;
using Registry.Web.Services.UpdateCheck;

namespace Registry.Web.Middlewares;

/// <summary>
/// Remembers the public URL the deployment is reached on (<see cref="ObservedBaseUrlStore"/>), for the update check that
/// runs on a node that serves no request. The URL is the one <see cref="IUrlBuilder"/> gives, so there is one rule for it.
/// Not needed, and not written, when an external URL is configured: that wins.
/// </summary>
/// <remarks>
/// Only an authenticated request records it, once the pipeline after this middleware (authentication included) has run:
/// the Host header of a request is the client's own, and an anonymous client must not choose the URL the update check
/// reports and its licence link points to.
/// </remarks>
public sealed class ObservedBaseUrlMiddleware : IMiddleware
{
    private readonly IUrlBuilder _urls;
    private readonly ObservedBaseUrlStore _store;
    private readonly HttpHostOptions _http;
    private readonly ILogger<ObservedBaseUrlMiddleware> _logger;

    public ObservedBaseUrlMiddleware(IUrlBuilder urls, ObservedBaseUrlStore store, IOptions<HttpHostOptions> http,
        ILogger<ObservedBaseUrlMiddleware> logger)
    {
        _urls = urls;
        _store = store;
        _http = http.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        await next(context);

        if (string.IsNullOrWhiteSpace(_http.ExternalUrlOverride) && context.User.Identity?.IsAuthenticated == true)
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
