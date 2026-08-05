using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Provides a durable store for HLS transcoding session state, enabling
/// HA recovery and lease-based ownership between pods.
/// </summary>
public interface ITranscodeSessionStore
{
    /// <summary>
    /// Gets a value indicating whether durable HA coordination is enabled.
    /// </summary>
    bool IsEnabled => false;

    /// <summary>
    /// Attempts to retrieve a transcoding session by its play session identifier.
    /// </summary>
    /// <param name="playSessionId">The play session identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The <see cref="TranscodeSession"/> if its recovery record still exists,
    /// including records with expired leases; otherwise <c>null</c>.
    /// </returns>
    Task<TranscodeSession?> TryGetAsync(string playSessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to take over ownership of an existing session by claiming the lease for
    /// <paramref name="claimingPod"/>. Takeover succeeds only when the session exists and
    /// its current lease has already expired.
    /// </summary>
    /// <param name="playSessionId">The play session identifier.</param>
    /// <param name="claimingPod">The name of the pod attempting to claim ownership.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// <c>true</c> if the takeover succeeded (the claiming pod now holds the lease);
    /// <c>false</c> if the session does not exist, its lease is still valid, or another
    /// concurrent caller already claimed it.
    /// </returns>
    Task<bool> TryTakeoverAsync(string playSessionId, string claimingPod, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically creates a session only when no recovery record exists.
    /// </summary>
    /// <param name="session">The initial durable session record.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><c>true</c> when the record was created; otherwise <c>false</c>.</returns>
    Task<bool> TryCreateAsync(TranscodeSession session, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>
    /// Persists a new or updated transcoding session.
    /// </summary>
    /// <param name="session">The session to store.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task SetAsync(TranscodeSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews the lease for an existing session, extending its
    /// <see cref="TranscodeSession.LeaseExpiresUtc"/> by the store's configured lease duration.
    /// </summary>
    /// <param name="playSessionId">The play session identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RenewLeaseAsync(string playSessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews a session only when <paramref name="ownerPod"/> still owns it.
    /// </summary>
    /// <param name="playSessionId">The play session identifier.</param>
    /// <param name="ownerPod">The expected current owner.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><c>true</c> when the owner still held a live lease and it was renewed.</returns>
    Task<bool> RenewLeaseAsync(string playSessionId, string ownerPod, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>
    /// Removes a transcoding session from the store.
    /// </summary>
    /// <param name="playSessionId">The play session identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteAsync(string playSessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a session only when <paramref name="ownerPod"/> still owns it.
    /// </summary>
    /// <param name="playSessionId">The play session identifier.</param>
    /// <param name="ownerPod">The expected current owner.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><c>true</c> when the owned record was deleted.</returns>
    Task<bool> DeleteAsync(string playSessionId, string ownerPod, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>
    /// Atomically advances the durable stream checkpoint when the caller owns
    /// the lease. Progress never moves backwards.
    /// </summary>
    /// <param name="playSessionId">The play session identifier.</param>
    /// <param name="ownerPod">The expected current owner.</param>
    /// <param name="manifestPath">The shared HLS manifest path.</param>
    /// <param name="segmentPathPrefix">The shared HLS segment path prefix.</param>
    /// <param name="completedSegmentIndex">The last completely served segment index.</param>
    /// <param name="durablePlaybackOffset">The corresponding playback offset in ticks.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><c>true</c> when the checkpoint was updated by the current owner.</returns>
    Task<bool> UpdateProgressAsync(
        string playSessionId,
        string ownerPod,
        string manifestPath,
        string segmentPathPrefix,
        int completedSegmentIndex,
        long durablePlaybackOffset,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>
    /// Returns all currently active transcoding sessions from the store.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// An enumerable of <see cref="TranscodeSession"/> objects representing all active sessions.
    /// Returns an empty enumerable if no sessions are active or if the store cannot be reached.
    /// </returns>
    Task<IEnumerable<TranscodeSession>> GetActiveSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a live stream session record so that takeover pods can identify and close
    /// streams that were opened on a pod that has since crashed or been evicted.
    /// </summary>
    /// <param name="session">The live stream session to store.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task SetLiveStreamAsync(LiveStreamSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to retrieve a live stream session by its live stream identifier and the
    /// session or play-session identifier that owns it.
    /// </summary>
    /// <param name="liveStreamId">The live stream identifier.</param>
    /// <param name="sessionIdOrPlaySessionId">The session identifier or play-session identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The <see cref="LiveStreamSession"/> if it exists; otherwise <c>null</c>.
    /// </returns>
    Task<LiveStreamSession?> TryGetLiveStreamAsync(string liveStreamId, string sessionIdOrPlaySessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the live stream session record for the given live stream and session identifier.
    /// This is called when the stream is closed, either by the owning pod or a takeover pod.
    /// </summary>
    /// <param name="liveStreamId">The live stream identifier.</param>
    /// <param name="sessionIdOrPlaySessionId">The session identifier or play-session identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteLiveStreamAsync(string liveStreamId, string sessionIdOrPlaySessionId, CancellationToken cancellationToken = default);
}
