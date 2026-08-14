using MediaBrowser.Controller.Library;

namespace Emby.Server.Implementations.Library;

/// <summary>
/// Selects canonical storage when the hot tier is not configured.
/// </summary>
public sealed class CanonicalPlaybackPathResolver : IPlaybackPathResolver
{
    /// <inheritdoc />
    public PlaybackPathResolution Resolve(in PlaybackPathRequest request)
        => new(request.CanonicalPath, false, "hot-cache-disabled");
}
