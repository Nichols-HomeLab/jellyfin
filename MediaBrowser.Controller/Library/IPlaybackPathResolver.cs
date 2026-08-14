namespace MediaBrowser.Controller.Library;

/// <summary>
/// Resolves a canonical media path to the local path used for one read.
/// </summary>
public interface IPlaybackPathResolver
{
    /// <summary>
    /// Resolves a path without changing the canonical library identity.
    /// </summary>
    /// <param name="request">The path resolution request.</param>
    /// <returns>The path to read.</returns>
    PlaybackPathResolution Resolve(in PlaybackPathRequest request);
}

/// <summary>
/// Describes one media path resolution.
/// </summary>
/// <param name="CanonicalPath">The authoritative library path.</param>
/// <param name="ExpectedLength">The expected file length, when known.</param>
/// <param name="Purpose">The reason the path will be opened.</param>
public readonly record struct PlaybackPathRequest(
    string CanonicalPath,
    long? ExpectedLength,
    PlaybackPathPurpose Purpose);

/// <summary>
/// Describes the selected read path.
/// </summary>
/// <param name="Path">The local path to read.</param>
/// <param name="IsHot">Whether <paramref name="Path"/> is a hot-tier path.</param>
/// <param name="Reason">A bounded machine-readable decision reason.</param>
public readonly record struct PlaybackPathResolution(
    string Path,
    bool IsHot,
    string Reason);

/// <summary>
/// Identifies how Jellyfin will use a resolved path.
/// </summary>
public enum PlaybackPathPurpose
{
    /// <summary>
    /// The main media file used for direct play or transcoding.
    /// </summary>
    MainMedia,

    /// <summary>
    /// An external subtitle, audio, or other sidecar stream.
    /// </summary>
    ExternalStream,

    /// <summary>
    /// A media file opened by FFprobe.
    /// </summary>
    Probe
}
