using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>Default <see cref="IUrlBuilder"/>: the external URL override, else the current request's host.</summary>
public class UrlBuilder : IUrlBuilder
{
    private readonly HttpHostOptions _http;
    private readonly IHttpContextAccessor _accessor;
    private readonly IBuildArtifactResolver _artifacts;

    public UrlBuilder(IOptions<HttpHostOptions> http, IHttpContextAccessor accessor, IBuildArtifactResolver artifacts)
    {
        _http = http.Value;
        _accessor = accessor;
        _artifacts = artifacts;
    }

    public string BuildFileUrl(string orgSlug, string dsSlug, string hash, string path) =>
        $"{GetLocalHost()}/orgs/{orgSlug}/ds/{dsSlug}/build/{hash}/" +
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    public IReadOnlyList<ArtifactLinkDto> ArtifactLinks(string orgSlug, string dsSlug, EntryBuildArtifacts build)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (!build.IsOwnBuild())
            return [];

        var own = build.Build!;
        return
        [
            .. own.Artifacts.Select(a => new ArtifactLinkDto
            {
                Role = a.Role,
                Url = BuildFileUrl(orgSlug, dsSlug, own.Hash!, a.Index is { Length: > 0 } index ? $"{a.Path}/{index}" : a.Path),
                MediaType = _artifacts.ServedMediaType(a)
            })
        ];
    }

    public string GenerateDatasetUrl(Dataset dataset, bool useDdbScheme = false)
    {
        return useDdbScheme ?
            $"{GetLocalHost(true)}/{dataset.Organization.Slug}/{dataset.Slug}" :
            $"{GetLocalHost(false)}/orgs/{dataset.Organization.Slug}/ds/{dataset.Slug}";
    }

    public string GetLocalHost(bool useDdbScheme = false)
    {
        bool isHttps;
        string host;

        if (!string.IsNullOrWhiteSpace(_http.ExternalUrlOverride))
        {
            var uri = new Uri(_http.ExternalUrlOverride);

            isHttps = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
            host = uri.Host;

            // The default port of either scheme is implied
            if (uri.Port != 443 && uri.Port != 80)
                host += $":{uri.Port}";
        }
        else
        {
            var context = _accessor.HttpContext;
            host = context?.Request.Host.ToString() ?? "localhost";
            isHttps = context?.Request.IsHttps ?? false;
        }

        var scheme = useDdbScheme ? isHttps ? "ddb" : "ddb+unsafe" : isHttps ? "https" : "http";

        return $"{scheme}://{host}";
    }
}
