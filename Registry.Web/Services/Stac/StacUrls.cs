using System;
using Registry.Web.Data.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Stac;

/// <summary>
/// Every URL of Registry's STAC documents (Registry owns them, libddb writes none): the STAC API routes,
/// the legacy per-dataset documents and the asset links. One instance per request (the host is the request's).
/// </summary>
public sealed class StacUrls
{
    private readonly IUrlBuilder _urls;
    private readonly Lazy<string> _host;

    public StacUrls(IUrlBuilder urls)
    {
        _urls = urls;
        _host = new Lazy<string>(() => urls.GetLocalHost());
    }

    /// <summary>The STAC API landing page (also the root of every document).</summary>
    public string Root => _host.Value + "/stac";

    public string Conformance => Root + "/conformance";
    public string Collections => Root + "/collections";
    public string Search => Root + "/search";
    public string ServiceDescription => _host.Value + "/swagger/v1/swagger.json";
    public string ServiceDocumentation => _host.Value + "/scalar/v1";

    public string Collection(string orgSlug, string dsSlug) => $"{Collections}/{orgSlug}/{dsSlug}";
    public string Items(string orgSlug, string dsSlug) => Collection(orgSlug, dsSlug) + "/items";

    public string Item(string orgSlug, string dsSlug, string path) =>
        Items(orgSlug, dsSlug) + "/" + StacIds.FeatureId(path);

    /// <summary>The legacy STAC document of a dataset (its Collection).</summary>
    public string LegacyCollection(Dataset ds) => DatasetApi(ds) + "/stac";

    /// <summary>The legacy STAC document of an entry (its Item).</summary>
    public string LegacyItem(Dataset ds, string path) => LegacyCollection(ds) + "/" + StacIds.LegacySegment(path);

    /// <summary>The asset link of an entry's file.</summary>
    public string Download(Dataset ds, string path) => DatasetApi(ds) + "/download?path=" + Uri.EscapeDataString(path);

    /// <summary>The asset link of an entry's thumbnail.</summary>
    public string Thumbnail(Dataset ds, string path) =>
        DatasetApi(ds) + "/thumb?path=" + Uri.EscapeDataString(path) + "&size=512";

    private string DatasetApi(Dataset ds) => _urls.GenerateDatasetUrl(ds);
}
