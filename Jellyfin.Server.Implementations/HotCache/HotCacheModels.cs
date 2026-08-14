using System;
using System.Collections.Generic;

#pragma warning disable SA1201, SA1402, SA1649 // Keep the coordinator's cohesive public contract in one discoverable file.

namespace Jellyfin.Server.Implementations.HotCache;

/// <summary>
/// Identifies a configured hot-cache storage adapter.
/// </summary>
public enum HotCacheBackend
{
    /// <summary>
    /// The Unraid <c>/temp</c> NFS share.
    /// </summary>
    UnraidTemp = 1,

    /// <summary>
    /// The optional CephFS volume.
    /// </summary>
    CephFs = 2
}

/// <summary>
/// Identifies why one user wants an episode kept hot.
/// </summary>
public enum HotCacheInterestReason
{
    /// <summary>
    /// The item is actively playing.
    /// </summary>
    ActivelyPlaying = 1,

    /// <summary>
    /// The item is partially watched.
    /// </summary>
    ContinueWatching = 2,

    /// <summary>
    /// Jellyfin reports the item as Next Up.
    /// </summary>
    NextUp = 3,

    /// <summary>
    /// The item is inside the configured next-episode window.
    /// </summary>
    Prefetch = 4,

    /// <summary>
    /// The episode was selected from a recently active series.
    /// </summary>
    RecentlyActive = 5,

    /// <summary>
    /// An administrator requested the item explicitly.
    /// </summary>
    Manual = 6
}

/// <summary>
/// Identifies the materialized lifecycle state of a hot-cache item.
/// </summary>
public enum HotCacheItemState
{
    /// <summary>
    /// The item has no published hot copy.
    /// </summary>
    Cold = 0,

    /// <summary>
    /// A promotion is waiting for a worker.
    /// </summary>
    Queued = 1,

    /// <summary>
    /// A worker is copying the item.
    /// </summary>
    Copying = 2,

    /// <summary>
    /// A validated hot copy is published.
    /// </summary>
    Copied = 3,

    /// <summary>
    /// An eviction is waiting or running.
    /// </summary>
    Evicting = 4,

    /// <summary>
    /// The disposable hot copy was removed.
    /// </summary>
    Evicted = 5,

    /// <summary>
    /// The latest work failed.
    /// </summary>
    Failed = 6
}

/// <summary>
/// Identifies work performed by a hot-cache worker.
/// </summary>
public enum HotCacheJobKind
{
    /// <summary>
    /// Copy and publish a canonical file.
    /// </summary>
    Promote = 1,

    /// <summary>
    /// Revalidate a published copy.
    /// </summary>
    Validate = 2,

    /// <summary>
    /// Remove a disposable hot copy.
    /// </summary>
    Evict = 3
}

/// <summary>
/// Identifies the result reported by a worker.
/// </summary>
public enum HotCacheJobOutcome
{
    /// <summary>
    /// The filesystem operation completed successfully.
    /// </summary>
    Succeeded = 1,

    /// <summary>
    /// The operation can be retried later.
    /// </summary>
    RetryableFailure = 2,

    /// <summary>
    /// The operation cannot succeed without a material input change.
    /// </summary>
    TerminalFailure = 3
}

/// <summary>
/// Describes why a hot item is removed.
/// </summary>
public enum HotCacheEvictionReason
{
    /// <summary>
    /// Playback completed and no remaining interest needs the episode.
    /// </summary>
    WatchedCompleted = 1,

    /// <summary>
    /// No included user played the series inside the activity window.
    /// </summary>
    InactiveAge = 2,

    /// <summary>
    /// The backend reached its configured free-space reserve.
    /// </summary>
    CapacityPressure = 3,

    /// <summary>
    /// The administrator reduced the configured lookahead.
    /// </summary>
    PolicyWindowReduced = 4,

    /// <summary>
    /// An administrator requested this eviction.
    /// </summary>
    Manual = 5,

    /// <summary>
    /// An administrator confirmed cleanup of an inactive backend.
    /// </summary>
    BackendCleanup = 6,

    /// <summary>
    /// The source identity changed and invalidated the hot copy.
    /// </summary>
    Stale = 7
}

/// <summary>
/// Identifies a durable audit event.
/// </summary>
public enum HotCacheEventType
{
    /// <summary>
    /// A hot copy was atomically published.
    /// </summary>
    Copied = 1,

    /// <summary>
    /// A hot copy was removed.
    /// </summary>
    Evicted = 2,

    /// <summary>
    /// Work failed.
    /// </summary>
    Failed = 3,

    /// <summary>
    /// Playback read the hot copy.
    /// </summary>
    WatchedHot = 4,

    /// <summary>
    /// Playback completed for the item.
    /// </summary>
    PlaybackCompleted = 5,

    /// <summary>
    /// Playback used canonical cold storage.
    /// </summary>
    ColdFallback = 6
}

/// <summary>
/// Reports one leased job outcome.
/// </summary>
/// <param name="JobId">The leased job ID.</param>
/// <param name="WorkerId">The lease owner.</param>
/// <param name="Outcome">Whether work succeeded or failed.</param>
/// <param name="BytesProcessed">Bytes copied, validated, or removed.</param>
/// <param name="Duration">Elapsed filesystem-operation time.</param>
/// <param name="EvictionReason">The reason for a successful eviction.</param>
/// <param name="ErrorSummary">A bounded operator-facing failure summary.</param>
public sealed record HotCacheJobAcknowledgement(
    Guid JobId,
    string WorkerId,
    HotCacheJobOutcome Outcome,
    long BytesProcessed,
    TimeSpan Duration,
    HotCacheEvictionReason? EvictionReason,
    string? ErrorSummary);

/// <summary>
/// Result of an idempotent job acknowledgement.
/// </summary>
public enum HotCacheAcknowledgeResult
{
    /// <summary>
    /// The state transition was newly applied.
    /// </summary>
    Applied = 1,

    /// <summary>
    /// The same successful result was already persisted.
    /// </summary>
    AlreadyApplied = 2
}

/// <summary>
/// Requests eviction of one materialized item.
/// </summary>
/// <param name="ItemId">The Jellyfin item ID.</param>
/// <param name="Reason">The auditable eviction reason.</param>
/// <param name="Priority">The eviction priority.</param>
public sealed record HotCacheEvictionRequest(Guid ItemId, HotCacheEvictionReason Reason, int Priority);

/// <summary>
/// One playback start, heartbeat, or completion observation.
/// </summary>
/// <param name="SessionId">The stable Jellyfin playback session ID.</param>
/// <param name="ItemId">The Jellyfin item ID.</param>
/// <param name="UserId">The stable Jellyfin user ID.</param>
/// <param name="HotPathUsed">Whether playback selected the hot copy.</param>
/// <param name="Completed">Whether Jellyfin reported the episode played.</param>
/// <param name="LeaseDuration">How long an incomplete session protects the item.</param>
public sealed record HotCachePlaybackObservation(
    string SessionId,
    Guid ItemId,
    Guid UserId,
    bool HotPathUsed,
    bool Completed,
    TimeSpan LeaseDuration);

/// <summary>
/// A worker's latest observation of one mounted backend.
/// </summary>
/// <param name="Backend">The observed backend.</param>
/// <param name="Mounted">Whether the mount exists.</param>
/// <param name="Readable">Whether a read probe succeeded.</param>
/// <param name="Writable">Whether a write/fsync/delete sentinel succeeded.</param>
/// <param name="TotalBytes">Actual filesystem capacity.</param>
/// <param name="AvailableBytes">Actual filesystem bytes available.</param>
/// <param name="ErrorSummary">A bounded error when unhealthy.</param>
public sealed record HotCacheBackendObservation(
    HotCacheBackend Backend,
    bool Mounted,
    bool Readable,
    bool Writable,
    long TotalBytes,
    long AvailableBytes,
    string? ErrorSummary);

/// <summary>
/// Latest persisted backend health and capacity.
/// </summary>
/// <param name="Backend">The observed backend.</param>
/// <param name="Mounted">Whether the mount exists.</param>
/// <param name="Readable">Whether a read probe succeeded.</param>
/// <param name="Writable">Whether a write probe succeeded.</param>
/// <param name="TotalBytes">Actual filesystem capacity.</param>
/// <param name="AvailableBytes">Actual bytes available.</param>
/// <param name="ObservedAt">When the worker observed it.</param>
/// <param name="ErrorSummary">The bounded health error.</param>
public sealed record HotCacheBackendSnapshot(
    HotCacheBackend Backend,
    bool Mounted,
    bool Readable,
    bool Writable,
    long TotalBytes,
    long AvailableBytes,
    DateTimeOffset ObservedAt,
    string? ErrorSummary);

/// <summary>
/// Requests one worker job lease.
/// </summary>
/// <param name="WorkerId">The stable worker identity.</param>
/// <param name="LeaseDuration">How long the claim remains exclusive without renewal.</param>
public sealed record HotCacheJobClaimRequest(string WorkerId, TimeSpan LeaseDuration);

/// <summary>
/// One exclusive, expiring worker claim.
/// </summary>
/// <param name="JobId">The durable job ID.</param>
/// <param name="ItemId">The Jellyfin item ID.</param>
/// <param name="Backend">The target backend.</param>
/// <param name="Kind">The requested work.</param>
/// <param name="CanonicalPath">The authoritative cold source path.</param>
/// <param name="RelativeHotPath">The destination path relative to the managed directory.</param>
/// <param name="SourceSize">The expected cold source size.</param>
/// <param name="SourceModifiedAt">The expected cold source modification time.</param>
/// <param name="EvictionReason">The durable reason for an eviction job.</param>
/// <param name="AttemptCount">The number of times this job has been claimed.</param>
/// <param name="LeaseOwner">The current worker identity.</param>
/// <param name="LeaseExpiresAt">When another worker may reclaim the job.</param>
public sealed record HotCacheJobLease(
    Guid JobId,
    Guid ItemId,
    HotCacheBackend Backend,
    HotCacheJobKind Kind,
    string CanonicalPath,
    string RelativeHotPath,
    long SourceSize,
    DateTimeOffset SourceModifiedAt,
    HotCacheEvictionReason? EvictionReason,
    int AttemptCount,
    string LeaseOwner,
    DateTimeOffset LeaseExpiresAt);

/// <summary>
/// One observed user interest that should reconcile to a desired hot item.
/// </summary>
/// <param name="ItemId">The stable Jellyfin episode ID.</param>
/// <param name="SeriesId">The stable Jellyfin series ID.</param>
/// <param name="UserId">The stable Jellyfin user ID.</param>
/// <param name="CanonicalPath">The authoritative path beneath <c>/media</c>.</param>
/// <param name="RelativeHotPath">The relative path beneath the managed cache directory.</param>
/// <param name="SourceSize">The expected source size in bytes.</param>
/// <param name="SourceModifiedAt">The expected source modification time.</param>
/// <param name="Reason">Why the user is interested.</param>
/// <param name="Priority">The effective promotion priority.</param>
/// <param name="ExpiresAt">When this interest stops qualifying.</param>
public sealed record HotCacheInterestRequest(
    Guid ItemId,
    Guid SeriesId,
    Guid UserId,
    string CanonicalPath,
    string RelativeHotPath,
    long SourceSize,
    DateTimeOffset SourceModifiedAt,
    HotCacheInterestReason Reason,
    int Priority,
    DateTimeOffset ExpiresAt);

/// <summary>
/// One current item row exposed through the coordinator interface.
/// </summary>
/// <param name="ItemId">The Jellyfin item ID.</param>
/// <param name="SeriesId">The Jellyfin series ID.</param>
/// <param name="State">The current materialized state.</param>
/// <param name="Priority">The effective priority.</param>
/// <param name="CopyCount">The number of completed promotions.</param>
/// <param name="EvictionCount">The number of completed evictions.</param>
/// <param name="WatchedAfterCopy">Whether playback used this residency after promotion.</param>
/// <param name="HotPlayCount">Distinct playback sessions that selected the hot copy.</param>
public sealed record HotCacheItemSnapshot(
    Guid ItemId,
    Guid SeriesId,
    HotCacheItemState State,
    int Priority,
    int CopyCount,
    int EvictionCount,
    bool WatchedAfterCopy,
    int HotPlayCount);

/// <summary>
/// Dashboard counts over current state and durable history.
/// </summary>
/// <param name="QueuedEpisodeCount">Items waiting for promotion.</param>
/// <param name="CopyingEpisodeCount">Items currently copying.</param>
/// <param name="CopiedEpisodeCount">Items with a published hot copy.</param>
/// <param name="EvictingEpisodeCount">Items waiting for or running eviction.</param>
/// <param name="EvictedEpisodeCount">Items whose latest residency ended in eviction.</param>
/// <param name="FailedEpisodeCount">Items whose latest work failed.</param>
/// <param name="HistoricalCopiedEpisodeCount">All successful promotions.</param>
/// <param name="HistoricalEvictionCount">All successful evictions.</param>
public sealed record HotCacheOperationalTotals(
    int QueuedEpisodeCount,
    int CopyingEpisodeCount,
    int CopiedEpisodeCount,
    int EvictingEpisodeCount,
    int EvictedEpisodeCount,
    int FailedEpisodeCount,
    int HistoricalCopiedEpisodeCount,
    int HistoricalEvictionCount);

/// <summary>
/// Per-series cache inventory and history.
/// </summary>
/// <param name="SeriesId">The Jellyfin series ID.</param>
/// <param name="CopiedEpisodeCount">Current published episodes.</param>
/// <param name="HistoricalCopiedEpisodeCount">All successful promotions.</param>
/// <param name="HistoricalEvictionCount">All successful evictions.</param>
/// <param name="CopiedBytes">Bytes copied over all residencies.</param>
public sealed record HotCacheSeriesSnapshot(
    Guid SeriesId,
    int CopiedEpisodeCount,
    int HistoricalCopiedEpisodeCount,
    int HistoricalEvictionCount,
    long CopiedBytes);

/// <summary>
/// One immutable operational history event.
/// </summary>
/// <param name="ItemId">The Jellyfin item ID.</param>
/// <param name="Type">The event type.</param>
/// <param name="OccurredAt">When the event was persisted.</param>
/// <param name="Bytes">The associated byte count.</param>
/// <param name="Duration">The associated operation duration.</param>
/// <param name="EvictionReason">The optional eviction reason.</param>
/// <param name="ErrorSummary">The bounded failure summary, when applicable.</param>
public sealed record HotCacheEventSnapshot(
    Guid ItemId,
    HotCacheEventType Type,
    DateTimeOffset OccurredAt,
    long Bytes,
    TimeSpan Duration,
    HotCacheEvictionReason? EvictionReason,
    string? ErrorSummary);

/// <summary>
/// Immutable hot-cache settings returned to callers.
/// </summary>
/// <param name="Enabled">Whether automatic cache work is enabled.</param>
/// <param name="Backend">The selected backend.</param>
/// <param name="ActivityWindow">How recently a series must have been played.</param>
/// <param name="ConfiguredMaximumLookahead">The administrator-selected maximum next-episode count.</param>
/// <param name="EffectiveLookahead">The current capacity-constrained next-episode count.</param>
/// <param name="MinimumFreeBytes">The absolute free-space reserve.</param>
/// <param name="ManagedDirectory">The managed directory relative to the fixed backend root.</param>
/// <param name="Version">The optimistic concurrency version.</param>
public sealed record HotCacheSettingsSnapshot(
    bool Enabled,
    HotCacheBackend Backend,
    TimeSpan ActivityWindow,
    int ConfiguredMaximumLookahead,
    int EffectiveLookahead,
    long MinimumFreeBytes,
    string ManagedDirectory,
    long Version);

/// <summary>
/// A complete optimistic update to administrator-controlled policy.
/// </summary>
/// <param name="ExpectedVersion">The version the administrator page last read.</param>
/// <param name="Enabled">Whether automatic work is enabled.</param>
/// <param name="Backend">The selected mounted backend.</param>
/// <param name="ActivityWindow">How recently a series must have been played.</param>
/// <param name="ConfiguredMaximumLookahead">The requested maximum next-episode count.</param>
/// <param name="MinimumFreeBytes">The absolute free-space reserve.</param>
/// <param name="ManagedDirectory">The managed directory relative to the fixed mount root.</param>
public sealed record HotCacheSettingsUpdate(
    long ExpectedVersion,
    bool Enabled,
    HotCacheBackend Backend,
    TimeSpan ActivityWindow,
    int ConfiguredMaximumLookahead,
    long MinimumFreeBytes,
    string ManagedDirectory);

/// <summary>
/// Indicates that another administrator or replica changed settings first.
/// </summary>
public sealed class HotCacheSettingsConflictException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HotCacheSettingsConflictException"/> class.
    /// </summary>
    public HotCacheSettingsConflictException()
        : base("Hot-cache settings changed after they were read.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HotCacheSettingsConflictException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public HotCacheSettingsConflictException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HotCacheSettingsConflictException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying exception.</param>
    public HotCacheSettingsConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// One consistent view of hot-cache settings and operational state.
/// </summary>
/// <param name="Settings">The current settings.</param>
/// <param name="Items">The current item inventory.</param>
/// <param name="InterestCount">The number of durable user/reason interests.</param>
/// <param name="PendingJobCount">The number of unclaimed jobs.</param>
/// <param name="Totals">Current and historical aggregate counts.</param>
/// <param name="Series">Per-series aggregates.</param>
/// <param name="Events">The retained audit history.</param>
/// <param name="ActivePlaybackLeaseCount">Unexpired playback leases.</param>
/// <param name="Backends">Latest backend health and real capacity.</param>
public sealed record HotCacheDashboardSnapshot(
    HotCacheSettingsSnapshot Settings,
    IReadOnlyList<HotCacheItemSnapshot> Items,
    int InterestCount,
    int PendingJobCount,
    HotCacheOperationalTotals Totals,
    IReadOnlyList<HotCacheSeriesSnapshot> Series,
    IReadOnlyList<HotCacheEventSnapshot> Events,
    int ActivePlaybackLeaseCount,
    IReadOnlyList<HotCacheBackendSnapshot> Backends);

#pragma warning restore SA1201, SA1402, SA1649
