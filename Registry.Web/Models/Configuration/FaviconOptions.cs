using Newtonsoft.Json;

namespace Registry.Web.Models.Configuration;

/// <summary>
/// Modern minimal favicon / web-manifest configuration.
/// Each href points to a publicly served URL — typical deployments drop the
/// referenced files under <c>{StoragePath}/branding/</c> and reference them
/// via the <c>/branding/...</c> URL prefix.
/// </summary>
public class FaviconOptions
{
    /// <summary>URL of the .ico fallback favicon.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string FaviconIco { get; set; }

    /// <summary>URL of the 16×16 PNG favicon.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string Favicon16 { get; set; }

    /// <summary>URL of the 32×32 PNG favicon.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string Favicon32 { get; set; }

    /// <summary>URL of the 180×180 apple-touch-icon PNG.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string AppleTouchIcon { get; set; }

    /// <summary>URL of the PWA web manifest (typically <c>site.webmanifest</c>).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string Manifest { get; set; }

    /// <summary>Theme color emitted as <c>&lt;meta name="theme-color"&gt;</c>.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string ThemeColor { get; set; }
}
