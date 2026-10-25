#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using Registry.Web.Services.UpdateCheck;

namespace Registry.Web.Middlewares;

/// <summary>
/// Remembers the public URL the deployment is reached on (<see cref="ObservedBaseUrlStore"/>), for the update check that
/// runs on a node that serves no request. The URL is the one <see cref="IUrlBuilder"/> gives, so there is one rule for it.
/// Not needed, and not written, when an external URL is configured: that wins.
/// </summary>
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

    public Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (string.IsNullOrWhiteSpace(_http.ExternalUrlOverride))
        {
            try
            {
                _store.Observe(_urls.GetLocalHost());
            }
            catch (Exception ex)
            {
                // A request never fails because of this
                _logger.LogDebug(ex, "Cannot record the public URL of the deployment");
            }
        }

        return next(context);
    }
}
