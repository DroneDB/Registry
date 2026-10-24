using System;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Registry.Web.Test.Support;

/// <summary>
/// TestServer leaves <c>HttpContext.Connection.RemoteIpAddress</c> null. This filter runs ahead of
/// the whole application pipeline (it is registered first, so it is the outermost startup filter)
/// and sets the peer address from the <see cref="HeaderName"/> header when present, so tests can
/// emulate the TCP peer (e.g. a reverse proxy on 127.0.0.1 vs. a direct client).
/// Without the header the peer address is left untouched (null).
/// </summary>
internal sealed class TestPeerIpStartupFilter : IStartupFilter
{
    public const string HeaderName = "X-Test-Peer-IP";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            var raw = context.Request.Headers[HeaderName].ToString();

            if (!string.IsNullOrWhiteSpace(raw))
            {
                if (!IPAddress.TryParse(raw.Trim(), out var address))
                    throw new InvalidOperationException($"Malformed {HeaderName} header: '{raw}'");

                context.Connection.RemoteIpAddress = address;
            }

            return nextMiddleware(context);
        });

        next(app);
    };
}
