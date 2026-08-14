using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Server.Implementations.HotCache;

/// <summary>
/// Owns durable hot-cache policy, work, and operational history.
/// </summary>
public interface IHotCacheCoordinator
{
    /// <summary>
    /// Applies any pending module-owned database migrations.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces administrator settings when the caller's version is current.
    /// </summary>
    /// <param name="update">The complete versioned settings update.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The newly persisted settings.</returns>
    Task<HotCacheSettingsSnapshot> UpdateSettingsAsync(HotCacheSettingsUpdate update, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records one user's current interest and atomically reconciles the desired promotion.
    /// </summary>
    /// <param name="request">The observed item interest.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    Task ReconcileInterestAsync(HotCacheInterestRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims the highest-priority available job under an expiring worker lease.
    /// </summary>
    /// <param name="request">The worker identity and lease duration.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The claimed job, or <see langword="null"/> when no work is available.</returns>
    Task<HotCacheJobLease?> ClaimJobAsync(HotCacheJobClaimRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotently records the outcome of work performed under a valid lease.
    /// </summary>
    /// <param name="acknowledgement">The job result and operational measurements.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the outcome was newly applied.</returns>
    Task<HotCacheAcknowledgeResult> AcknowledgeJobAsync(HotCacheJobAcknowledgement acknowledgement, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles one materialized item to an eviction job.
    /// </summary>
    /// <param name="request">The item and auditable eviction reason.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    Task QueueEvictionAsync(HotCacheEvictionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes or completes a playback lease and records whether the hot path was used.
    /// </summary>
    /// <param name="observation">The playback observation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    Task RecordPlaybackAsync(HotCachePlaybackObservation observation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the latest real filesystem observation for one backend.
    /// </summary>
    /// <param name="observation">The backend observation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    Task RecordBackendStatusAsync(HotCacheBackendObservation observation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a bounded batch of expired operational events without changing durable item statistics.
    /// </summary>
    /// <param name="retention">How long operational events remain available.</param>
    /// <param name="batchSize">The maximum number of events to delete.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of events deleted.</returns>
    Task<int> PruneEventsAsync(TimeSpan retention, int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets one consistent administrator-facing snapshot.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The current dashboard snapshot.</returns>
    Task<HotCacheDashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
