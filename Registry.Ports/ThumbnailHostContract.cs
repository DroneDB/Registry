namespace Registry.Ports;

/// <summary>
/// Wire contract of the remote ThumbnailGenerator host (<c>POST /generate-thumbnail</c>), shared by the
/// client adapter (<c>RemoteThumbnailGenerator</c>) and the host's request handler.
/// </summary>
public static class ThumbnailHostContract
{
    /// <summary>Request header carrying the shared token; the host rejects requests without it.</summary>
    public const string TokenHeaderName = "X-Thumbnail-Token";
}
