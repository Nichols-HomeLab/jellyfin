using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Server.Implementations.HotCache;
using Npgsql;
using Xunit;

namespace Jellyfin.Database.Tests.PostgreSQL;

/// <summary>
/// Exercises the hot-cache coordinator interface against PostgreSQL.
/// </summary>
[Trait("Category", "RequiresPostgreSQL")]
public sealed class HotCacheCoordinatorPostgreSqlTests : IAsyncLifetime
{
    private readonly string _schema = "hot_cache_test_" + Guid.NewGuid().ToString("N");
    private readonly ManualTimeProvider _timeProvider = new(DateTimeOffset.Parse("2026-08-14T12:00:00Z", CultureInfo.InvariantCulture));
    private NpgsqlDataSource? _administrativeDataSource;
    private NpgsqlDataSource? _testDataSource;
    private IHotCacheCoordinator? _coordinator;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("JELLYFIN_HOT_CACHE_TEST_POSTGRES")
            ?? throw new InvalidOperationException("JELLYFIN_HOT_CACHE_TEST_POSTGRES is required.");

        _administrativeDataSource = NpgsqlDataSource.Create(connectionString);
        await using (var command = _administrativeDataSource.CreateCommand($"CREATE SCHEMA \"{_schema}\""))
        {
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = _schema
        };
        _testDataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        _coordinator = new PostgreSqlHotCacheCoordinator(_testDataSource, _timeProvider);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_testDataSource is not null)
        {
            await _testDataSource.DisposeAsync();
        }

        if (_administrativeDataSource is not null)
        {
            await using var command = _administrativeDataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE");
            await command.ExecuteNonQueryAsync();
            await _administrativeDataSource.DisposeAsync();
        }
    }

    /// <summary>
    /// A fresh coordinator applies its migration repeatedly and exposes policy defaults.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task InitializeAsync_IsRepeatableAndCreatesDefaultSettings()
    {
        await _coordinator!.InitializeAsync();
        await _coordinator.InitializeAsync();

        var snapshot = await _coordinator.GetSnapshotAsync();

        Assert.False(snapshot.Settings.Enabled);
        Assert.Equal(HotCacheBackend.UnraidTemp, snapshot.Settings.Backend);
        Assert.Equal(TimeSpan.FromDays(14), snapshot.Settings.ActivityWindow);
        Assert.Equal(6, snapshot.Settings.ConfiguredMaximumLookahead);
        Assert.Equal(6, snapshot.Settings.EffectiveLookahead);
        Assert.Equal(150L * 1024 * 1024 * 1024, snapshot.Settings.MinimumFreeBytes);
        Assert.Equal("jellyfin-hot-cache", snapshot.Settings.ManagedDirectory);
    }

    /// <summary>
    /// Concurrent Jellyfin replicas deduplicate one desired promotion at the coordinator seam.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ReconcileInterestAsync_ConcurrentCallsCreateOneItemInterestAndJob()
    {
        await _coordinator!.InitializeAsync();
        var itemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var interest = new HotCacheInterestRequest(
            itemId,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "/media/tv/Lioness/S03E02.mkv",
            "tv/Lioness/S03E02.mkv",
            2_867_374_331,
            DateTimeOffset.Parse("2026-08-14T08:00:00Z", CultureInfo.InvariantCulture),
            HotCacheInterestReason.NextUp,
            80,
            DateTimeOffset.Parse("2026-08-28T08:00:00Z", CultureInfo.InvariantCulture));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _coordinator.ReconcileInterestAsync(interest)));

        var snapshot = await _coordinator.GetSnapshotAsync();
        var item = Assert.Single(snapshot.Items);
        Assert.Equal(itemId, item.ItemId);
        Assert.Equal(HotCacheItemState.Queued, item.State);
        Assert.Equal(1, snapshot.InterestCount);
        Assert.Equal(1, snapshot.PendingJobCount);
    }

    /// <summary>
    /// Only one worker claims a job, and another worker can reclaim it after lease expiry.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ClaimJobAsync_ExcludesConcurrentWorkersAndReclaimsExpiredLease()
    {
        await _coordinator!.InitializeAsync();
        await _coordinator.ReconcileInterestAsync(CreateInterest());

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            _coordinator.ClaimJobAsync(new HotCacheJobClaimRequest($"worker-{index}", TimeSpan.FromMinutes(5)))));
        var first = Assert.Single(claims.OfType<HotCacheJobLease>());
        Assert.Equal(1, first.AttemptCount);
        Assert.Equal("/media/tv/NCIS Origins/S02E18.mkv", first.CanonicalPath);
        Assert.Equal("tv/NCIS Origins/S02E18.mkv", first.RelativeHotPath);
        Assert.Equal(4_995_141_688, first.SourceSize);
        Assert.Null(first.EvictionReason);
        Assert.Null(await _coordinator.ClaimJobAsync(new HotCacheJobClaimRequest("late-worker", TimeSpan.FromMinutes(5))));

        _timeProvider.Advance(TimeSpan.FromMinutes(6));
        var reclaimed = await _coordinator.ClaimJobAsync(new HotCacheJobClaimRequest("replacement-worker", TimeSpan.FromMinutes(5)));

        Assert.NotNull(reclaimed);
        Assert.Equal(first.JobId, reclaimed.JobId);
        Assert.Equal(2, reclaimed.AttemptCount);
        Assert.Equal("replacement-worker", reclaimed.LeaseOwner);
    }

    /// <summary>
    /// Successful promotion and eviction transitions are idempotent and retain history.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task AcknowledgeJobAsync_PreservesCopiedAndEvictedHistory()
    {
        await _coordinator!.InitializeAsync();
        await _coordinator.ReconcileInterestAsync(CreateInterest());
        var promotion = await _coordinator.ClaimJobAsync(new HotCacheJobClaimRequest("worker-a", TimeSpan.FromMinutes(5)));
        Assert.NotNull(promotion);

        var copied = new HotCacheJobAcknowledgement(
            promotion.JobId,
            "worker-a",
            HotCacheJobOutcome.Succeeded,
            4_995_141_688,
            TimeSpan.FromSeconds(37),
            null,
            null);
        Assert.Equal(HotCacheAcknowledgeResult.Applied, await _coordinator.AcknowledgeJobAsync(copied));
        Assert.Equal(HotCacheAcknowledgeResult.AlreadyApplied, await _coordinator.AcknowledgeJobAsync(copied));

        await _coordinator.QueueEvictionAsync(new HotCacheEvictionRequest(
            promotion.ItemId,
            HotCacheEvictionReason.WatchedCompleted,
            100));
        var eviction = await _coordinator.ClaimJobAsync(new HotCacheJobClaimRequest("worker-b", TimeSpan.FromMinutes(5)));
        Assert.NotNull(eviction);
        Assert.Equal(HotCacheJobKind.Evict, eviction.Kind);
        Assert.Equal(HotCacheEvictionReason.WatchedCompleted, eviction.EvictionReason);
        var evicted = new HotCacheJobAcknowledgement(
            eviction.JobId,
            "worker-b",
            HotCacheJobOutcome.Succeeded,
            4_995_141_688,
            TimeSpan.FromSeconds(2),
            HotCacheEvictionReason.WatchedCompleted,
            null);
        Assert.Equal(HotCacheAcknowledgeResult.Applied, await _coordinator.AcknowledgeJobAsync(evicted));
        Assert.Equal(HotCacheAcknowledgeResult.AlreadyApplied, await _coordinator.AcknowledgeJobAsync(evicted));

        var snapshot = await _coordinator.GetSnapshotAsync();
        var item = Assert.Single(snapshot.Items);
        Assert.Equal(HotCacheItemState.Evicted, item.State);
        Assert.Equal(1, item.CopyCount);
        Assert.Equal(1, item.EvictionCount);
        Assert.Equal(1, snapshot.Totals.HistoricalCopiedEpisodeCount);
        Assert.Equal(1, snapshot.Totals.HistoricalEvictionCount);
        var series = Assert.Single(snapshot.Series);
        Assert.Equal(1, series.HistoricalCopiedEpisodeCount);
        Assert.Collection(
            snapshot.Events,
            itemEvent => Assert.Equal(HotCacheEventType.Copied, itemEvent.Type),
            itemEvent => Assert.Equal(HotCacheEventType.Evicted, itemEvent.Type));
    }

    /// <summary>
    /// Administrator settings use optimistic versioning and reject unsafe managed paths.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateSettingsAsync_IsVersionedAndRejectsUnsafeDirectory()
    {
        await _coordinator!.InitializeAsync();
        var initial = (await _coordinator.GetSnapshotAsync()).Settings;
        var update = new HotCacheSettingsUpdate(
            initial.Version,
            true,
            HotCacheBackend.UnraidTemp,
            TimeSpan.FromDays(10),
            2,
            175L * 1024 * 1024 * 1024,
            "jellyfin-hot-cache-alt");

        var changed = await _coordinator.UpdateSettingsAsync(update);

        Assert.True(changed.Enabled);
        Assert.Equal(initial.Version + 1, changed.Version);
        Assert.Equal(TimeSpan.FromDays(10), changed.ActivityWindow);
        Assert.Equal(2, changed.ConfiguredMaximumLookahead);
        Assert.Equal(175L * 1024 * 1024 * 1024, changed.MinimumFreeBytes);
        Assert.Equal("jellyfin-hot-cache-alt", changed.ManagedDirectory);
        await Assert.ThrowsAsync<HotCacheSettingsConflictException>(() => _coordinator.UpdateSettingsAsync(update));
        await Assert.ThrowsAsync<ArgumentException>(() => _coordinator.UpdateSettingsAsync(update with
        {
            ExpectedVersion = changed.Version,
            ManagedDirectory = "../media"
        }));
    }

    /// <summary>
    /// Playback leases block eviction and backend observations expose real filesystem capacity.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task RecordPlaybackAsync_ProtectsActiveReadsAndTracksHotWatchAndCapacity()
    {
        await _coordinator!.InitializeAsync();
        var interest = CreateInterest();
        await _coordinator.ReconcileInterestAsync(interest);
        var promotion = await _coordinator.ClaimJobAsync(new HotCacheJobClaimRequest("worker-a", TimeSpan.FromMinutes(5)));
        Assert.NotNull(promotion);
        await _coordinator.AcknowledgeJobAsync(new HotCacheJobAcknowledgement(
            promotion.JobId,
            "worker-a",
            HotCacheJobOutcome.Succeeded,
            interest.SourceSize,
            TimeSpan.FromSeconds(30),
            null,
            null));

        var playback = new HotCachePlaybackObservation(
            "session-1",
            interest.ItemId,
            interest.UserId,
            true,
            false,
            TimeSpan.FromMinutes(5));
        await _coordinator.RecordPlaybackAsync(playback);
        await _coordinator.RecordPlaybackAsync(playback);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _coordinator.QueueEvictionAsync(
            new HotCacheEvictionRequest(interest.ItemId, HotCacheEvictionReason.WatchedCompleted, 100)));

        await _coordinator.RecordBackendStatusAsync(new HotCacheBackendObservation(
            HotCacheBackend.UnraidTemp,
            true,
            true,
            true,
            774_033_637_376,
            641_442_250_752,
            null));
        var active = await _coordinator.GetSnapshotAsync();
        var activeItem = Assert.Single(active.Items);
        Assert.True(activeItem.WatchedAfterCopy);
        Assert.Equal(1, activeItem.HotPlayCount);
        Assert.Equal(1, active.ActivePlaybackLeaseCount);
        var backend = Assert.Single(active.Backends);
        Assert.Equal(641_442_250_752, backend.AvailableBytes);

        _timeProvider.Advance(TimeSpan.FromMinutes(6));
        await _coordinator.RecordPlaybackAsync(playback with { Completed = true });
        await _coordinator.QueueEvictionAsync(new HotCacheEvictionRequest(
            interest.ItemId,
            HotCacheEvictionReason.WatchedCompleted,
            100));
        var completed = await _coordinator.GetSnapshotAsync();
        Assert.Equal(0, completed.ActivePlaybackLeaseCount);
        Assert.Contains(completed.Events, itemEvent => itemEvent.Type == HotCacheEventType.PlaybackCompleted);
    }

    /// <summary>
    /// Failure text is bounded and event retention never erases durable item statistics.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task PruneEventsAsync_DeletesExpiredEventsButKeepsDurableStatistics()
    {
        await _coordinator!.InitializeAsync();
        var interest = CreateInterest();
        await _coordinator.ReconcileInterestAsync(interest);
        var promotion = await _coordinator.ClaimJobAsync(new HotCacheJobClaimRequest("worker-a", TimeSpan.FromMinutes(5)));
        Assert.NotNull(promotion);
        await _coordinator.AcknowledgeJobAsync(new HotCacheJobAcknowledgement(
            promotion.JobId,
            "worker-a",
            HotCacheJobOutcome.Succeeded,
            interest.SourceSize,
            TimeSpan.FromSeconds(30),
            null,
            null));
        await _coordinator.QueueEvictionAsync(new HotCacheEvictionRequest(
            interest.ItemId,
            HotCacheEvictionReason.CapacityPressure,
            100));
        var eviction = await _coordinator.ClaimJobAsync(new HotCacheJobClaimRequest("worker-b", TimeSpan.FromMinutes(5)));
        Assert.NotNull(eviction);
        await _coordinator.AcknowledgeJobAsync(new HotCacheJobAcknowledgement(
            eviction.JobId,
            "worker-b",
            HotCacheJobOutcome.TerminalFailure,
            0,
            TimeSpan.FromSeconds(1),
            HotCacheEvictionReason.CapacityPressure,
            new string('x', 5_000)));

        var failed = await _coordinator.GetSnapshotAsync();
        Assert.Equal(1, failed.Totals.FailedEpisodeCount);
        Assert.Equal(1, failed.Totals.HistoricalCopiedEpisodeCount);
        Assert.Equal(4_096, failed.Events.Single(itemEvent => itemEvent.Type == HotCacheEventType.Failed).ErrorSummary!.Length);

        _timeProvider.Advance(TimeSpan.FromDays(91));
        Assert.Equal(2, await _coordinator.PruneEventsAsync(TimeSpan.FromDays(90), 100));
        var retained = await _coordinator.GetSnapshotAsync();
        Assert.Empty(retained.Events);
        Assert.Equal(1, retained.Totals.HistoricalCopiedEpisodeCount);
        Assert.Equal(1, Assert.Single(retained.Items).CopyCount);
    }

    private static HotCacheInterestRequest CreateInterest()
    {
        return new HotCacheInterestRequest(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            "/media/tv/NCIS Origins/S02E18.mkv",
            "tv/NCIS Origins/S02E18.mkv",
            4_995_141_688,
            DateTimeOffset.Parse("2026-08-14T08:00:00Z", CultureInfo.InvariantCulture),
            HotCacheInterestReason.NextUp,
            80,
            DateTimeOffset.Parse("2026-08-28T08:00:00Z", CultureInfo.InvariantCulture));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan duration)
        {
            _utcNow = _utcNow.Add(duration);
        }
    }
}
