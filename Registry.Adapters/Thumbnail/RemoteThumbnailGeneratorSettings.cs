using Registry.Ports;

namespace Registry.Adapters.Thumbnail;

/// <summary>
/// Settings of the client of a remote ThumbnailGenerator host.
/// </summary>
public class RemoteThumbnailGeneratorSettings
{
    /// <summary>
    /// Request header carrying <see cref="Token"/>; the ThumbnailGenerator host rejects requests without it.
    /// </summary>
    public const string TokenHeaderName = ThumbnailHostContract.TokenHeaderName;

    /// <summary>
    /// URL of the host's <c>generate-thumbnail</c> endpoint. The remote generator is registered only when the
    /// setting is present, and the registration sets it, so it is never read unset.
    /// </summary>
    public string Url { get; set; } = null!;

    /// <summary>
    /// Shared secret sent in <see cref="TokenHeaderName"/> (the <c>ThumbnailGeneratorToken</c> setting).
    /// </summary>
    public string? Token { get; set; }
}
