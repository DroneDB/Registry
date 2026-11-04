namespace Registry.Web.Models.Configuration;

/// <summary>Thumbnails: the generator and the defaults (keys of the <c>AppSettings</c> section).</summary>
public sealed class ThumbnailOptions : IHasConfigurationDefaults
{
    public const int DefaultThumbnailSizePixels = 512;

    /// <summary>The dataset thumbnail candidates used when <see cref="DatasetThumbnailCandidates"/> is not set.</summary>
    public static readonly string[] DefaultDatasetThumbnailCandidates =
    [
        "thumbnail.webp", "thumbnail.jpg", "thumbnail.png",
        "cover.webp", "cover.jpg", "cover.png"
    ];

    /// <summary>Remote thumbnail generator URL (if null the local one is used).</summary>
    public string? RemoteThumbnailGeneratorUrl { get; set; }

    /// <summary>
    /// Shared secret between the web server and the ThumbnailGenerator host (header
    /// <c>X-Thumbnail-Token</c>). Generated on the first start when empty and stored in
    /// <c>appsettings.json</c>; the ThumbnailGenerator host refuses to start without it.
    /// </summary>
    public string? ThumbnailGeneratorToken { get; set; }

    /// <summary>Default thumbnail size in pixels when no size is specified.</summary>
    public int DefaultThumbnailSize { get; set; } = DefaultThumbnailSizePixels;

    /// <summary>
    /// File names (in the dataset root) considered as dataset thumbnail candidates, in priority order;
    /// <see cref="DefaultDatasetThumbnailCandidates"/> when not set (applied by <see cref="ApplyDefaults"/>).
    /// </summary>
    public string[]? DatasetThumbnailCandidates { get; set; }

    /// <summary><see cref="DatasetThumbnailCandidates"/> with the default applied.</summary>
    public string[] Candidates => ConfigurationLists.ConfiguredOrDefault(DatasetThumbnailCandidates, DefaultDatasetThumbnailCandidates);

    public void ApplyDefaults() => DatasetThumbnailCandidates = Candidates;
}
