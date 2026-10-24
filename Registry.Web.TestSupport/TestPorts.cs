#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test;

/// <summary>The focused ports that replaced <c>IUtils</c> (RQ-12), built over a test's collaborators.</summary>
public static class TestPorts
{
    /// <summary>The dataset resolver over <paramref name="context"/>, without the lookup cache (every lookup queries).</summary>
    public static DatasetResolver Datasets(RegistryContext context) => new(context, NoLookups.Instance);

    /// <summary>
    /// The URL builder over the host options (none: the request's host) and the request.
    /// </summary>
    public static UrlBuilder Urls(IOptions<HttpHostOptions>? http = null, IHttpContextAccessor? accessor = null) =>
        new(http ?? Microsoft.Extensions.Options.Options.Create(new HttpHostOptions()), accessor ?? new HttpContextAccessor());

    /// <summary>The storage quota of the current user (<paramref name="auth"/>) over <paramref name="context"/>.</summary>
    public static StorageQuota Quota(IAuthManager auth, RegistryContext context, IOptions<AccountOptions> accounts,
        IDatasetSummaryProvider summaries) => new(auth, context, accounts, summaries);

    /// <summary>
    /// The three ports over the collaborators <c>WebUtils</c> took (no lookup cache): what a test that built the
    /// whole <c>IUtils</c> builds now.
    /// </summary>
    public static WebPorts All(IAuthManager auth, RegistryContext context, IOptions<AccountOptions> accounts,
        IOptions<HttpHostOptions> http, IHttpContextAccessor accessor, IDatasetSummaryProvider summaries) =>
        new(Datasets(context), Urls(http, accessor), Quota(auth, context, accounts, summaries));

    /// <summary>The build artifact resolver over the fake's entry types (or <paramref name="ddbWrapper"/>) and the real file system.</summary>
    public static BuildArtifactResolver Artifacts(Registry.Ports.IDdbWrapper? ddbWrapper = null) =>
        new(ddbWrapper ?? new Registry.Adapters.Testing.FakeDdbWrapper(), new Registry.Adapters.FileSystem());

    /// <summary>A lookup cache that caches nothing.</summary>
    private sealed class NoLookups : IDatasetLookupCache
    {
        public static readonly NoLookups Instance = new();

        public Task<Dataset?> GetOrLoadAsync(string orgSlug, string dsSlug,
            Func<CancellationToken, Task<Dataset?>> load, CancellationToken ct = default) => load(ct);

        public void InvalidateDataset(string orgSlug, string dsSlug)
        {
        }

        public void InvalidateOrganization(string orgSlug)
        {
        }

        public void InvalidateAll()
        {
        }
    }
}

/// <summary>The ports that replaced <c>IUtils</c>, over one set of collaborators.</summary>
public sealed record WebPorts(DatasetResolver Datasets, UrlBuilder Urls, StorageQuota Quota);
