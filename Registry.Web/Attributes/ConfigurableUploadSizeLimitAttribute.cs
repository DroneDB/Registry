using System;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Attributes;

/// <summary>
/// Applies the configured upload size limit (<see cref="AppSettings.MaxRequestBodySize"/>) to the
/// current request. A <c>null</c> configured value means "unlimited".
/// </summary>
/// <remarks>
/// Unlike <c>[RequestFormLimits]</c> (which only caps the buffered multipart form reader and is
/// bypassed by streaming uploads), this sets the Kestrel connection-level body size limit, so the
/// limit is enforced for both buffered and streamed uploads. Use it in place of
/// <c>[DisableRequestSizeLimit]</c> on upload endpoints.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ConfigurableUploadSizeLimitAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var feature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();

        // The feature is absent on some servers (e.g. TestServer) and read-only once the body
        // has started being read; in both cases there is nothing to do.
        if (feature is null || feature.IsReadOnly)
            return;

        var settings = context.HttpContext.RequestServices
            .GetService<IOptions<AppSettings>>()?.Value;

        // null => unlimited (previous [DisableRequestSizeLimit] behaviour when unset)
        feature.MaxRequestBodySize = settings?.MaxRequestBodySize;
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
        // No-op
    }
}
