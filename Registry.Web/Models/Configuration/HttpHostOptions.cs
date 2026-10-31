namespace Registry.Web.Models.Configuration;

/// <summary>How the web server is reached: public URL, request limits, CORS and the reverse proxy (keys of the <c>AppSettings</c> section).</summary>
public sealed class HttpHostOptions : IHasConfigurationDefaults
{
    /// <summary>The reverse proxies trusted when <see cref="KnownProxies"/> is not set.</summary>
    public static readonly string[] DefaultKnownProxies = ["127.0.0.1", "::1"];

    /// <summary>Overrides the external URL (absolute when set).</summary>
    public string? ExternalUrlOverride { get; set; }

    /// <summary>Max request body size.</summary>
    public long? MaxRequestBodySize { get; set; }

    /// <summary>Allowed CORS origins. When null or empty, all origins are allowed.</summary>
    public string[]? AllowedOrigins { get; set; }

    /// <summary>
    /// IP addresses of the reverse proxies whose <c>X-Forwarded-For</c> / <c>X-Forwarded-Proto</c>
    /// headers are trusted (the client address and scheme they forward replace the connection's).
    /// When null or empty, <see cref="DefaultKnownProxies"/> (loopback, a proxy on the same host)
    /// applies. When set, it replaces the default: list loopback too if the proxy also connects
    /// over it. Applied by <see cref="ApplyDefaults"/>.
    /// </summary>
    public string[]? KnownProxies { get; set; }

    /// <summary>
    /// Networks (CIDR, e.g. <c>172.16.0.0/12</c>) whose addresses are trusted as reverse proxies, in
    /// addition to <see cref="KnownProxies"/>. Needed when the proxy's address is not fixed: behind a
    /// port published by Docker the app sees the bridge gateway (<c>172.x.0.1</c>) as the peer, not
    /// loopback. Null or empty: no network is trusted.
    /// </summary>
    public string[]? KnownNetworks { get; set; }

    public void ApplyDefaults()
    {
        KnownProxies = ConfigurationLists.ConfiguredOrDefault(KnownProxies, DefaultKnownProxies);
        KnownNetworks ??= [];
    }
}
