namespace Registry.Adapters.Thumbnail;

/// <summary>
/// Settings of the client of a remote ThumbnailGenerator host.
/// </summary>
public class RemoteThumbnailGeneratorSettings
{
    /// <summary>
    /// Request header carrying <see cref="Token"/>; the ThumbnailGenerator host rejects requests without it.
    /// </summary>
    public const string TokenHeaderName = "X-Thumbnail-Token";

    /// <summary>
    /// URL of the host's <c>generate-thumbnail</c> endpoint.
    /// </summary>
    public string Url { get; set; }

    /// <summary>
    /// Shared secret sent in <see cref="TokenHeaderName"/> (the <c>ThumbnailGeneratorToken</c> setting).
    /// </summary>
    public string? Token { get; set; }
}
