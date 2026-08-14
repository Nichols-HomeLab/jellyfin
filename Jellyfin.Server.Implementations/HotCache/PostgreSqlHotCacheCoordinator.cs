using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace Jellyfin.Server.Implementations.HotCache;

/// <summary>
/// PostgreSQL implementation of the hot-cache coordinator module.
/// </summary>
public sealed class PostgreSqlHotCacheCoordinator : IHotCacheCoordinator
{
    private const long DefaultMinimumFreeBytes = 150L * 1024 * 1024 * 1024;

    private const string InitialMigrationSql = """
        CREATE TABLE IF NOT EXISTS hot_cache_schema_migrations (
            version integer PRIMARY KEY,
            applied_at timestamptz NOT NULL
        );

        CREATE TABLE IF NOT EXISTS hot_cache_settings (
            singleton boolean PRIMARY KEY DEFAULT TRUE CHECK (singleton),
            enabled boolean NOT NULL,
            backend smallint NOT NULL CHECK (backend IN (1, 2)),
            activity_window_days integer NOT NULL CHECK (activity_window_days BETWEEN 1 AND 3650),
            configured_maximum_lookahead integer NOT NULL CHECK (configured_maximum_lookahead BETWEEN 0 AND 6),
            minimum_free_bytes bigint NOT NULL CHECK (minimum_free_bytes >= 0),
            managed_directory text NOT NULL CHECK (length(managed_directory) BETWEEN 1 AND 512),
            settings_version bigint NOT NULL,
            updated_at timestamptz NOT NULL
        );

        CREATE TABLE IF NOT EXISTS hot_cache_items (
            item_id uuid NOT NULL,
            backend smallint NOT NULL CHECK (backend IN (1, 2)),
            series_id uuid NOT NULL,
            canonical_path text NOT NULL,
            relative_hot_path text NOT NULL,
            source_size bigint NOT NULL CHECK (source_size >= 0),
            source_modified_at timestamptz NOT NULL,
            state smallint NOT NULL CHECK (state BETWEEN 0 AND 6),
            effective_priority integer NOT NULL,
            last_interest_at timestamptz NOT NULL,
            copied_size bigint,
            copy_count integer NOT NULL DEFAULT 0,
            copy_started_at timestamptz,
            copy_completed_at timestamptz,
            copy_duration_ms bigint,
            cumulative_copied_bytes bigint NOT NULL DEFAULT 0,
            eviction_count integer NOT NULL DEFAULT 0,
            last_eviction_reason smallint CHECK (last_eviction_reason BETWEEN 1 AND 7),
            last_evicted_at timestamptz,
            last_eviction_duration_ms bigint,
            watched_after_copy boolean NOT NULL DEFAULT FALSE,
            first_hot_play_at timestamptz,
            last_hot_play_at timestamptz,
            hot_play_count integer NOT NULL DEFAULT 0,
            last_cold_fallback_at timestamptz,
            PRIMARY KEY (item_id, backend)
        );

        CREATE TABLE IF NOT EXISTS hot_cache_interests (
            item_id uuid NOT NULL,
            backend smallint NOT NULL,
            user_id uuid NOT NULL,
            reason smallint NOT NULL CHECK (reason BETWEEN 1 AND 6),
            priority integer NOT NULL,
            first_observed_at timestamptz NOT NULL,
            last_observed_at timestamptz NOT NULL,
            expires_at timestamptz NOT NULL,
            PRIMARY KEY (item_id, backend, user_id, reason),
            FOREIGN KEY (item_id, backend)
                REFERENCES hot_cache_items (item_id, backend) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS hot_cache_jobs (
            job_id uuid PRIMARY KEY,
            item_id uuid NOT NULL,
            backend smallint NOT NULL,
            kind smallint NOT NULL CHECK (kind BETWEEN 1 AND 3),
            eviction_reason smallint CHECK (eviction_reason BETWEEN 1 AND 7),
            state smallint NOT NULL CHECK (state BETWEEN 1 AND 4),
            priority integer NOT NULL,
            attempt_count integer NOT NULL DEFAULT 0,
            next_attempt_at timestamptz NOT NULL,
            lease_owner text,
            lease_expires_at timestamptz,
            progress_bytes bigint NOT NULL DEFAULT 0,
            error_summary text,
            created_at timestamptz NOT NULL,
            updated_at timestamptz NOT NULL,
            FOREIGN KEY (item_id, backend)
                REFERENCES hot_cache_items (item_id, backend) ON DELETE CASCADE
        );

        CREATE UNIQUE INDEX IF NOT EXISTS hot_cache_jobs_one_active
            ON hot_cache_jobs (item_id, backend, kind)
            WHERE state IN (1, 2);

        CREATE TABLE IF NOT EXISTS hot_cache_events (
            event_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            item_id uuid NOT NULL,
            backend smallint NOT NULL,
            job_id uuid,
            session_id text,
            event_type smallint NOT NULL CHECK (event_type BETWEEN 1 AND 6),
            eviction_reason smallint CHECK (eviction_reason BETWEEN 1 AND 7),
            bytes bigint NOT NULL DEFAULT 0,
            duration_ms bigint NOT NULL DEFAULT 0,
            occurred_at timestamptz NOT NULL,
            error_summary text,
            FOREIGN KEY (item_id, backend)
                REFERENCES hot_cache_items (item_id, backend) ON DELETE CASCADE
        );

        CREATE UNIQUE INDEX IF NOT EXISTS hot_cache_events_one_job_result
            ON hot_cache_events (job_id, event_type)
            WHERE job_id IS NOT NULL AND event_type IN (1, 2);

        CREATE UNIQUE INDEX IF NOT EXISTS hot_cache_events_one_session_signal
            ON hot_cache_events (session_id, event_type)
            WHERE session_id IS NOT NULL AND event_type IN (4, 5, 6);

        CREATE TABLE IF NOT EXISTS hot_cache_playback_leases (
            session_id text PRIMARY KEY,
            item_id uuid NOT NULL,
            backend smallint NOT NULL,
            user_id uuid NOT NULL,
            expires_at timestamptz NOT NULL,
            last_refreshed_at timestamptz NOT NULL,
            FOREIGN KEY (item_id, backend)
                REFERENCES hot_cache_items (item_id, backend) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS hot_cache_backend_status (
            backend smallint PRIMARY KEY CHECK (backend IN (1, 2)),
            mounted boolean NOT NULL,
            readable boolean NOT NULL,
            writable boolean NOT NULL,
            total_bytes bigint NOT NULL CHECK (total_bytes >= 0),
            available_bytes bigint NOT NULL CHECK (available_bytes >= 0),
            observed_at timestamptz NOT NULL,
            error_summary text
        );
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlHotCacheCoordinator"/> class.
    /// </summary>
    /// <param name="dataSource">The shared Jellyfin PostgreSQL data source.</param>
    /// <param name="timeProvider">The source of wall-clock time.</param>
    public PostgreSqlHotCacheCoordinator(NpgsqlDataSource dataSource, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _dataSource = dataSource;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        await using (var advisoryLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(current_database() || ':' || current_schema() || ':jellyfin_hot_cache'))", connection, transaction))
        {
            await advisoryLock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var migration = new NpgsqlCommand(InitialMigrationSql, connection, transaction))
        {
            await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var now = _timeProvider.GetUtcNow();
        await using (var defaults = new NpgsqlCommand(
            """
            INSERT INTO hot_cache_settings (
                singleton, enabled, backend, activity_window_days,
                configured_maximum_lookahead, minimum_free_bytes,
                managed_directory, settings_version, updated_at)
            VALUES (TRUE, FALSE, 1, 14, 6, @minimum_free_bytes,
                'jellyfin-hot-cache', 1, @updated_at)
            ON CONFLICT (singleton) DO NOTHING;

            INSERT INTO hot_cache_schema_migrations (version, applied_at)
            VALUES (1, @updated_at)
            ON CONFLICT (version) DO NOTHING;
            """,
            connection,
            transaction))
        {
            defaults.Parameters.AddWithValue("minimum_free_bytes", DefaultMinimumFreeBytes);
            defaults.Parameters.AddWithValue("updated_at", now);
            await defaults.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HotCacheSettingsSnapshot> UpdateSettingsAsync(
        HotCacheSettingsUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ValidateSettings(update);
        var activityWindowDays = checked((int)update.ActivityWindow.TotalDays);
        var now = _timeProvider.GetUtcNow();
        await using var command = _dataSource.CreateCommand(
            """
            UPDATE hot_cache_settings
               SET enabled = @enabled,
                   backend = @backend,
                   activity_window_days = @activity_window_days,
                   configured_maximum_lookahead = @configured_maximum_lookahead,
                   minimum_free_bytes = @minimum_free_bytes,
                   managed_directory = @managed_directory,
                   settings_version = settings_version + 1,
                   updated_at = @updated_at
             WHERE singleton = TRUE
               AND settings_version = @expected_version
            RETURNING enabled, backend, activity_window_days,
                      configured_maximum_lookahead, minimum_free_bytes,
                      managed_directory, settings_version;
            """);
        command.Parameters.AddWithValue("enabled", update.Enabled);
        command.Parameters.AddWithValue("backend", (short)update.Backend);
        command.Parameters.AddWithValue("activity_window_days", activityWindowDays);
        command.Parameters.AddWithValue("configured_maximum_lookahead", update.ConfiguredMaximumLookahead);
        command.Parameters.AddWithValue("minimum_free_bytes", update.MinimumFreeBytes);
        command.Parameters.AddWithValue("managed_directory", update.ManagedDirectory);
        command.Parameters.AddWithValue("updated_at", now);
        command.Parameters.AddWithValue("expected_version", update.ExpectedVersion);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new HotCacheSettingsConflictException();
        }

        return ReadSettings(reader);
    }

    /// <inheritdoc />
    public async Task ReconcileInterestAsync(HotCacheInterestRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourceSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Source size cannot be negative.");
        }

        if (request.Priority is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Priority must be between 0 and 100.");
        }

        if (!Enum.IsDefined(request.Reason))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Eviction reason is not supported.");
        }

        var now = _timeProvider.GetUtcNow();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            WITH selected_backend AS (
                SELECT backend FROM hot_cache_settings WHERE singleton = TRUE
            )
            INSERT INTO hot_cache_items (
                item_id, backend, series_id, canonical_path, relative_hot_path,
                source_size, source_modified_at, state, effective_priority,
                last_interest_at)
            SELECT @item_id, backend, @series_id, @canonical_path,
                   @relative_hot_path, @source_size, @source_modified_at,
                   1, @priority, @now
              FROM selected_backend
            ON CONFLICT (item_id, backend) DO UPDATE SET
                series_id = EXCLUDED.series_id,
                canonical_path = EXCLUDED.canonical_path,
                relative_hot_path = EXCLUDED.relative_hot_path,
                source_size = EXCLUDED.source_size,
                source_modified_at = EXCLUDED.source_modified_at,
                state = CASE
                    WHEN hot_cache_items.state IN (0, 5, 6) THEN 1
                    ELSE hot_cache_items.state
                END,
                effective_priority = GREATEST(hot_cache_items.effective_priority, EXCLUDED.effective_priority),
                last_interest_at = EXCLUDED.last_interest_at;

            INSERT INTO hot_cache_interests (
                item_id, backend, user_id, reason, priority,
                first_observed_at, last_observed_at, expires_at)
            SELECT @item_id, backend, @user_id, @reason, @priority,
                   @now, @now, @expires_at
              FROM hot_cache_settings
             WHERE singleton = TRUE
            ON CONFLICT (item_id, backend, user_id, reason) DO UPDATE SET
                priority = EXCLUDED.priority,
                last_observed_at = EXCLUDED.last_observed_at,
                expires_at = EXCLUDED.expires_at;

            INSERT INTO hot_cache_jobs (
                job_id, item_id, backend, kind, state, priority,
                next_attempt_at, created_at, updated_at)
            SELECT @job_id, @item_id, backend, 1, 1, @priority,
                   @now, @now, @now
              FROM hot_cache_settings
             WHERE singleton = TRUE
            ON CONFLICT (item_id, backend, kind) WHERE state IN (1, 2)
            DO UPDATE SET
                priority = GREATEST(hot_cache_jobs.priority, EXCLUDED.priority),
                next_attempt_at = LEAST(hot_cache_jobs.next_attempt_at, EXCLUDED.next_attempt_at),
                updated_at = EXCLUDED.updated_at;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("item_id", request.ItemId);
        command.Parameters.AddWithValue("series_id", request.SeriesId);
        command.Parameters.AddWithValue("user_id", request.UserId);
        command.Parameters.AddWithValue("canonical_path", request.CanonicalPath);
        command.Parameters.AddWithValue("relative_hot_path", request.RelativeHotPath);
        command.Parameters.AddWithValue("source_size", request.SourceSize);
        command.Parameters.AddWithValue("source_modified_at", request.SourceModifiedAt);
        command.Parameters.AddWithValue("reason", (short)request.Reason);
        command.Parameters.AddWithValue("priority", request.Priority);
        command.Parameters.AddWithValue("expires_at", request.ExpiresAt);
        command.Parameters.AddWithValue("job_id", Guid.NewGuid());
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HotCacheJobLease?> ClaimJobAsync(HotCacheJobClaimRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.WorkerId) || request.WorkerId.Length > 128)
        {
            throw new ArgumentException("Worker ID must contain 1 to 128 non-whitespace characters.", nameof(request));
        }

        if (request.LeaseDuration <= TimeSpan.Zero || request.LeaseDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Lease duration must be positive and no longer than one hour.");
        }

        var now = _timeProvider.GetUtcNow();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            WITH candidate AS (
                SELECT job_id
                  FROM hot_cache_jobs
                 WHERE next_attempt_at <= @now
                   AND (state = 1 OR (state = 2 AND lease_expires_at <= @now))
                 ORDER BY priority DESC, created_at, job_id
                   FOR UPDATE SKIP LOCKED
                 LIMIT 1
            ), claimed AS (
                UPDATE hot_cache_jobs AS job
                   SET state = 2,
                       lease_owner = @worker_id,
                       lease_expires_at = @lease_expires_at,
                       attempt_count = job.attempt_count + 1,
                       updated_at = @now
                  FROM candidate
                 WHERE job.job_id = candidate.job_id
                RETURNING job.job_id, job.item_id, job.backend, job.kind,
                          job.eviction_reason, job.attempt_count,
                          job.lease_owner, job.lease_expires_at
            )
            SELECT claimed.job_id, claimed.item_id, claimed.backend, claimed.kind,
                   item.canonical_path, item.relative_hot_path, item.source_size,
                   item.source_modified_at, claimed.eviction_reason,
                   claimed.attempt_count, claimed.lease_owner, claimed.lease_expires_at
              FROM claimed
              JOIN hot_cache_items AS item
                ON item.item_id = claimed.item_id AND item.backend = claimed.backend;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("worker_id", request.WorkerId);
        command.Parameters.AddWithValue("lease_expires_at", now.Add(request.LeaseDuration));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var lease = new HotCacheJobLease(
            reader.GetGuid(0),
            reader.GetGuid(1),
            (HotCacheBackend)reader.GetInt16(2),
            (HotCacheJobKind)reader.GetInt16(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6),
            await reader.GetFieldValueAsync<DateTimeOffset>(7, cancellationToken).ConfigureAwait(false),
            await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false) ? null : (HotCacheEvictionReason)reader.GetInt16(8),
            reader.GetInt32(9),
            reader.GetString(10),
            await reader.GetFieldValueAsync<DateTimeOffset>(11, cancellationToken).ConfigureAwait(false));
        await reader.DisposeAsync().ConfigureAwait(false);

        await using (var itemCommand = new NpgsqlCommand(
            """
            UPDATE hot_cache_items
               SET state = CASE @kind
                   WHEN 1 THEN 2
                   WHEN 3 THEN 4
                   ELSE state
               END,
                   copy_started_at = CASE WHEN @kind = 1 THEN @now ELSE copy_started_at END
             WHERE item_id = @item_id AND backend = @backend;
            """,
            connection,
            transaction))
        {
            itemCommand.Parameters.AddWithValue("kind", (short)lease.Kind);
            itemCommand.Parameters.AddWithValue("now", now);
            itemCommand.Parameters.AddWithValue("item_id", lease.ItemId);
            itemCommand.Parameters.AddWithValue("backend", (short)lease.Backend);
            await itemCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return lease;
    }

    /// <inheritdoc />
    public async Task QueueEvictionAsync(HotCacheEvictionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Priority is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Priority must be between 0 and 100.");
        }

        var now = _timeProvider.GetUtcNow();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            WITH target AS (
                UPDATE hot_cache_items AS item
                   SET state = 4
                  FROM hot_cache_settings AS settings
                 WHERE settings.singleton = TRUE
                   AND item.item_id = @item_id
                   AND item.backend = settings.backend
                   AND item.state IN (3, 4)
                   AND NOT EXISTS (
                       SELECT 1
                         FROM hot_cache_playback_leases AS lease
                        WHERE lease.item_id = item.item_id
                          AND lease.backend = item.backend
                          AND lease.expires_at > @now)
                RETURNING item.item_id, item.backend
            )
            INSERT INTO hot_cache_jobs (
                job_id, item_id, backend, kind, eviction_reason, state, priority,
                next_attempt_at, created_at, updated_at)
            SELECT @job_id, item_id, backend, 3, @eviction_reason, 1, @priority,
                   @now, @now, @now
              FROM target
            ON CONFLICT (item_id, backend, kind) WHERE state IN (1, 2)
            DO UPDATE SET
                priority = GREATEST(hot_cache_jobs.priority, EXCLUDED.priority),
                eviction_reason = EXCLUDED.eviction_reason,
                updated_at = EXCLUDED.updated_at;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("item_id", request.ItemId);
        command.Parameters.AddWithValue("job_id", Guid.NewGuid());
        command.Parameters.AddWithValue("priority", request.Priority);
        command.Parameters.AddWithValue("eviction_reason", (short)request.Reason);
        command.Parameters.AddWithValue("now", now);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new InvalidOperationException("Only a copied item on the selected backend can be queued for eviction.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordPlaybackAsync(
        HotCachePlaybackObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (string.IsNullOrWhiteSpace(observation.SessionId) || observation.SessionId.Length > 256)
        {
            throw new ArgumentException("Session ID must contain 1 to 256 non-whitespace characters.", nameof(observation));
        }

        if (!observation.Completed
            && (observation.LeaseDuration <= TimeSpan.Zero || observation.LeaseDuration > TimeSpan.FromHours(1)))
        {
            throw new ArgumentOutOfRangeException(nameof(observation), "Playback lease must be positive and no longer than one hour.");
        }

        var now = _timeProvider.GetUtcNow();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        if (observation.Completed)
        {
            await using var completed = new NpgsqlCommand(
                """
                WITH target AS (
                    SELECT item.item_id, item.backend
                      FROM hot_cache_items AS item
                      JOIN hot_cache_settings AS settings
                        ON settings.singleton = TRUE AND settings.backend = item.backend
                     WHERE item.item_id = @item_id
                ), removed_lease AS (
                    DELETE FROM hot_cache_playback_leases
                     WHERE session_id = @session_id
                ), removed_interest AS (
                    DELETE FROM hot_cache_interests AS interest
                     USING target
                     WHERE interest.item_id = target.item_id
                       AND interest.backend = target.backend
                       AND interest.user_id = @user_id
                )
                INSERT INTO hot_cache_events (
                    item_id, backend, session_id, event_type, occurred_at)
                SELECT item_id, backend, @session_id, 5, @now
                  FROM target
                ON CONFLICT (session_id, event_type)
                    WHERE session_id IS NOT NULL AND event_type IN (4, 5, 6)
                DO NOTHING;
                """,
                connection,
                transaction);
            completed.Parameters.AddWithValue("session_id", observation.SessionId);
            completed.Parameters.AddWithValue("item_id", observation.ItemId);
            completed.Parameters.AddWithValue("user_id", observation.UserId);
            completed.Parameters.AddWithValue("now", now);
            await completed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await using var playback = new NpgsqlCommand(
                """
                WITH target AS (
                    SELECT item.item_id, item.backend
                      FROM hot_cache_items AS item
                      JOIN hot_cache_settings AS settings
                        ON settings.singleton = TRUE AND settings.backend = item.backend
                     WHERE item.item_id = @item_id
                ), refreshed_lease AS (
                    INSERT INTO hot_cache_playback_leases (
                        session_id, item_id, backend, user_id,
                        expires_at, last_refreshed_at)
                    SELECT @session_id, item_id, backend, @user_id,
                           @expires_at, @now
                      FROM target
                    ON CONFLICT (session_id) DO UPDATE SET
                        item_id = EXCLUDED.item_id,
                        backend = EXCLUDED.backend,
                        user_id = EXCLUDED.user_id,
                        expires_at = EXCLUDED.expires_at,
                        last_refreshed_at = EXCLUDED.last_refreshed_at
                ), inserted_event AS (
                    INSERT INTO hot_cache_events (
                        item_id, backend, session_id, event_type, occurred_at)
                    SELECT item_id, backend, @session_id, @event_type, @now
                      FROM target
                    ON CONFLICT (session_id, event_type)
                        WHERE session_id IS NOT NULL AND event_type IN (4, 5, 6)
                    DO NOTHING
                    RETURNING item_id, backend
                )
                UPDATE hot_cache_items AS item
                   SET watched_after_copy = item.watched_after_copy OR @hot_path_used,
                       first_hot_play_at = CASE
                           WHEN @hot_path_used THEN coalesce(item.first_hot_play_at, @now)
                           ELSE item.first_hot_play_at
                       END,
                       last_hot_play_at = CASE WHEN @hot_path_used THEN @now ELSE item.last_hot_play_at END,
                       hot_play_count = item.hot_play_count + CASE WHEN @hot_path_used THEN 1 ELSE 0 END,
                       last_cold_fallback_at = CASE WHEN @hot_path_used THEN item.last_cold_fallback_at ELSE @now END
                  FROM inserted_event
                 WHERE item.item_id = inserted_event.item_id
                   AND item.backend = inserted_event.backend;
                """,
                connection,
                transaction);
            playback.Parameters.AddWithValue("session_id", observation.SessionId);
            playback.Parameters.AddWithValue("item_id", observation.ItemId);
            playback.Parameters.AddWithValue("user_id", observation.UserId);
            playback.Parameters.AddWithValue("expires_at", now.Add(observation.LeaseDuration));
            playback.Parameters.AddWithValue("now", now);
            playback.Parameters.AddWithValue("event_type", (short)(observation.HotPathUsed
                ? HotCacheEventType.WatchedHot
                : HotCacheEventType.ColdFallback));
            playback.Parameters.AddWithValue("hot_path_used", observation.HotPathUsed);
            await playback.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordBackendStatusAsync(
        HotCacheBackendObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!Enum.IsDefined(observation.Backend))
        {
            throw new ArgumentOutOfRangeException(nameof(observation), "Backend is not supported.");
        }

        if (observation.TotalBytes < 0
            || observation.AvailableBytes < 0
            || observation.AvailableBytes > observation.TotalBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(observation), "Backend capacity values are inconsistent.");
        }

        await using var command = _dataSource.CreateCommand(
            """
            INSERT INTO hot_cache_backend_status (
                backend, mounted, readable, writable, total_bytes,
                available_bytes, observed_at, error_summary)
            VALUES (
                @backend, @mounted, @readable, @writable, @total_bytes,
                @available_bytes, @observed_at, @error_summary)
            ON CONFLICT (backend) DO UPDATE SET
                mounted = EXCLUDED.mounted,
                readable = EXCLUDED.readable,
                writable = EXCLUDED.writable,
                total_bytes = EXCLUDED.total_bytes,
                available_bytes = EXCLUDED.available_bytes,
                observed_at = EXCLUDED.observed_at,
                error_summary = EXCLUDED.error_summary;
            """);
        command.Parameters.AddWithValue("backend", (short)observation.Backend);
        command.Parameters.AddWithValue("mounted", observation.Mounted);
        command.Parameters.AddWithValue("readable", observation.Readable);
        command.Parameters.AddWithValue("writable", observation.Writable);
        command.Parameters.AddWithValue("total_bytes", observation.TotalBytes);
        command.Parameters.AddWithValue("available_bytes", observation.AvailableBytes);
        command.Parameters.AddWithValue("observed_at", _timeProvider.GetUtcNow());
        command.Parameters.Add("error_summary", NpgsqlDbType.Text).Value = (object?)BoundErrorOrNull(observation.ErrorSummary) ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> PruneEventsAsync(
        TimeSpan retention,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), "Event retention must be positive.");
        }

        if (batchSize is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Event pruning batch size must be between 1 and 10000.");
        }

        await using var command = _dataSource.CreateCommand(
            """
            WITH expired AS (
                SELECT event_id
                  FROM hot_cache_events
                 WHERE occurred_at < @cutoff
                 ORDER BY event_id
                 FOR UPDATE SKIP LOCKED
                 LIMIT @batch_size
            )
            DELETE FROM hot_cache_events AS event
                  USING expired
             WHERE event.event_id = expired.event_id;
            """);
        command.Parameters.AddWithValue("cutoff", _timeProvider.GetUtcNow().Subtract(retention));
        command.Parameters.AddWithValue("batch_size", batchSize);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HotCacheAcknowledgeResult> AcknowledgeJobAsync(
        HotCacheJobAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (acknowledgement.BytesProcessed < 0 || acknowledgement.Duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(acknowledgement), "Byte and duration measurements cannot be negative.");
        }

        var now = _timeProvider.GetUtcNow();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        HotCacheJobKind kind;
        Guid itemId;
        HotCacheBackend backend;
        HotCacheEvictionReason? evictionReason;
        short state;
        string? leaseOwner;
        DateTimeOffset? leaseExpiresAt;
        await using (var select = new NpgsqlCommand(
            """
            SELECT kind, item_id, backend, eviction_reason, state, lease_owner, lease_expires_at
              FROM hot_cache_jobs
             WHERE job_id = @job_id
               FOR UPDATE;
            """,
            connection,
            transaction))
        {
            select.Parameters.AddWithValue("job_id", acknowledgement.JobId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The hot-cache job does not exist.");
            }

            kind = (HotCacheJobKind)reader.GetInt16(0);
            itemId = reader.GetGuid(1);
            backend = (HotCacheBackend)reader.GetInt16(2);
            evictionReason = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
                ? null
                : (HotCacheEvictionReason)reader.GetInt16(3);
            state = reader.GetInt16(4);
            leaseOwner = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5);
            leaseExpiresAt = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
                ? null
                : await reader.GetFieldValueAsync<DateTimeOffset>(6, cancellationToken).ConfigureAwait(false);
        }

        if (state == 3 && acknowledgement.Outcome == HotCacheJobOutcome.Succeeded)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return HotCacheAcknowledgeResult.AlreadyApplied;
        }

        if (state != 2
            || !string.Equals(leaseOwner, acknowledgement.WorkerId, StringComparison.Ordinal)
            || leaseExpiresAt <= now)
        {
            throw new InvalidOperationException("The worker does not hold a current lease for this job.");
        }

        if (kind == HotCacheJobKind.Evict
            && (evictionReason is null
                || (acknowledgement.EvictionReason is not null
                    && acknowledgement.EvictionReason != evictionReason)))
        {
            throw new InvalidOperationException("The eviction acknowledgement does not match the queued reason.");
        }

        if (acknowledgement.Outcome == HotCacheJobOutcome.Succeeded)
        {
            await ApplySuccessfulJobAsync(
                connection,
                transaction,
                acknowledgement,
                kind,
                itemId,
                backend,
                evictionReason,
                now,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ApplyFailedJobAsync(
                connection,
                transaction,
                acknowledgement,
                kind,
                itemId,
                backend,
                now,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return HotCacheAcknowledgeResult.Applied;
    }

    /// <inheritdoc />
    public async Task<HotCacheDashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT enabled, backend, activity_window_days,
                   configured_maximum_lookahead, minimum_free_bytes,
                   managed_directory, settings_version
             FROM hot_cache_settings
             WHERE singleton = TRUE;
            """,
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The hot-cache database has not been initialized.");
        }

        var settings = ReadSettings(reader);
        await reader.DisposeAsync().ConfigureAwait(false);

        var items = new List<HotCacheItemSnapshot>();
        await using (var itemsCommand = new NpgsqlCommand(
            """
            SELECT item_id, series_id, state, effective_priority,
                   copy_count, eviction_count, watched_after_copy,
                   hot_play_count
              FROM hot_cache_items
             ORDER BY item_id, backend;
            """,
            connection,
            transaction))
        await using (var itemsReader = await itemsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await itemsReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new HotCacheItemSnapshot(
                    itemsReader.GetGuid(0),
                    itemsReader.GetGuid(1),
                    (HotCacheItemState)itemsReader.GetInt16(2),
                    itemsReader.GetInt32(3),
                    itemsReader.GetInt32(4),
                    itemsReader.GetInt32(5),
                    itemsReader.GetBoolean(6),
                    itemsReader.GetInt32(7)));
            }
        }

        var interestCount = await CountAsync(
            connection,
            transaction,
            "SELECT count(*)::integer FROM hot_cache_interests",
            cancellationToken).ConfigureAwait(false);
        var pendingJobCount = await CountAsync(
            connection,
            transaction,
            "SELECT count(*)::integer FROM hot_cache_jobs WHERE state = 1",
            cancellationToken).ConfigureAwait(false);
        int activePlaybackLeaseCount;
        await using (var leaseCountCommand = new NpgsqlCommand(
            "SELECT count(*)::integer FROM hot_cache_playback_leases WHERE expires_at > @now",
            connection,
            transaction))
        {
            leaseCountCommand.Parameters.AddWithValue("now", _timeProvider.GetUtcNow());
            activePlaybackLeaseCount = (int)(await leaseCountCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The playback lease count query returned no value."));
        }

        HotCacheOperationalTotals totals;
        await using (var totalsCommand = new NpgsqlCommand(
            """
            SELECT count(*) FILTER (WHERE state = 1)::integer,
                   count(*) FILTER (WHERE state = 2)::integer,
                   count(*) FILTER (WHERE state = 3)::integer,
                   count(*) FILTER (WHERE state = 4)::integer,
                   count(*) FILTER (WHERE state = 5)::integer,
                   count(*) FILTER (WHERE state = 6)::integer,
                   coalesce(sum(copy_count), 0)::integer,
                   coalesce(sum(eviction_count), 0)::integer
              FROM hot_cache_items;
            """,
            connection,
            transaction))
        await using (var totalsReader = await totalsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await totalsReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            totals = new HotCacheOperationalTotals(
                totalsReader.GetInt32(0),
                totalsReader.GetInt32(1),
                totalsReader.GetInt32(2),
                totalsReader.GetInt32(3),
                totalsReader.GetInt32(4),
                totalsReader.GetInt32(5),
                totalsReader.GetInt32(6),
                totalsReader.GetInt32(7));
        }

        var series = new List<HotCacheSeriesSnapshot>();
        await using (var seriesCommand = new NpgsqlCommand(
            """
            SELECT series_id,
                   count(*) FILTER (WHERE state = 3)::integer,
                   coalesce(sum(copy_count), 0)::integer,
                   coalesce(sum(eviction_count), 0)::integer,
                   coalesce(sum(cumulative_copied_bytes), 0)::bigint
              FROM hot_cache_items
             GROUP BY series_id
             ORDER BY series_id;
            """,
            connection,
            transaction))
        await using (var seriesReader = await seriesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await seriesReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                series.Add(new HotCacheSeriesSnapshot(
                    seriesReader.GetGuid(0),
                    seriesReader.GetInt32(1),
                    seriesReader.GetInt32(2),
                    seriesReader.GetInt32(3),
                    seriesReader.GetInt64(4)));
            }
        }

        var events = new List<HotCacheEventSnapshot>();
        await using (var eventsCommand = new NpgsqlCommand(
            """
            SELECT item_id, event_type, occurred_at, bytes, duration_ms,
                   eviction_reason, error_summary
              FROM hot_cache_events
             ORDER BY event_id;
            """,
            connection,
            transaction))
        await using (var eventsReader = await eventsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await eventsReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                events.Add(new HotCacheEventSnapshot(
                    eventsReader.GetGuid(0),
                    (HotCacheEventType)eventsReader.GetInt16(1),
                    await eventsReader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken).ConfigureAwait(false),
                    eventsReader.GetInt64(3),
                    TimeSpan.FromMilliseconds(eventsReader.GetInt64(4)),
                    await eventsReader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false)
                        ? null
                        : (HotCacheEvictionReason)eventsReader.GetInt16(5),
                    await eventsReader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : eventsReader.GetString(6)));
            }
        }

        var backends = new List<HotCacheBackendSnapshot>();
        await using (var backendsCommand = new NpgsqlCommand(
            """
            SELECT backend, mounted, readable, writable, total_bytes,
                   available_bytes, observed_at, error_summary
              FROM hot_cache_backend_status
             ORDER BY backend;
            """,
            connection,
            transaction))
        await using (var backendsReader = await backendsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await backendsReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                backends.Add(new HotCacheBackendSnapshot(
                    (HotCacheBackend)backendsReader.GetInt16(0),
                    backendsReader.GetBoolean(1),
                    backendsReader.GetBoolean(2),
                    backendsReader.GetBoolean(3),
                    backendsReader.GetInt64(4),
                    backendsReader.GetInt64(5),
                    await backendsReader.GetFieldValueAsync<DateTimeOffset>(6, cancellationToken).ConfigureAwait(false),
                    await backendsReader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) ? null : backendsReader.GetString(7)));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new HotCacheDashboardSnapshot(
            settings,
            items,
            interestCount,
            pendingJobCount,
            totals,
            series,
            events,
            activePlaybackLeaseCount,
            backends);
    }

    private static async Task ApplySuccessfulJobAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HotCacheJobAcknowledgement acknowledgement,
        HotCacheJobKind kind,
        Guid itemId,
        HotCacheBackend backend,
        HotCacheEvictionReason? evictionReason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var job = new NpgsqlCommand(
            """
            UPDATE hot_cache_jobs
               SET state = 3,
                   progress_bytes = @bytes,
                   lease_owner = NULL,
                   lease_expires_at = NULL,
                   error_summary = NULL,
                   updated_at = @now
             WHERE job_id = @job_id;
            """,
            connection,
            transaction))
        {
            job.Parameters.AddWithValue("bytes", acknowledgement.BytesProcessed);
            job.Parameters.AddWithValue("now", now);
            job.Parameters.AddWithValue("job_id", acknowledgement.JobId);
            await job.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var durationMilliseconds = checked((long)acknowledgement.Duration.TotalMilliseconds);
        var itemSql = kind == HotCacheJobKind.Evict
            ? """
                UPDATE hot_cache_items
                   SET state = 5,
                       copied_size = NULL,
                       eviction_count = eviction_count + 1,
                       last_eviction_reason = @eviction_reason,
                       last_evicted_at = @now,
                       last_eviction_duration_ms = @duration_ms
                 WHERE item_id = @item_id AND backend = @backend;
                """
            : """
                UPDATE hot_cache_items
                   SET state = 3,
                       copied_size = @bytes,
                       copy_count = copy_count + 1,
                       copy_completed_at = @now,
                       copy_duration_ms = @duration_ms,
                       cumulative_copied_bytes = cumulative_copied_bytes + @bytes
                 WHERE item_id = @item_id AND backend = @backend;
                """;
        await using (var item = new NpgsqlCommand(itemSql, connection, transaction))
        {
            item.Parameters.AddWithValue("bytes", acknowledgement.BytesProcessed);
            item.Parameters.AddWithValue("duration_ms", durationMilliseconds);
            item.Parameters.AddWithValue("eviction_reason", (short)(evictionReason ?? HotCacheEvictionReason.Manual));
            item.Parameters.AddWithValue("now", now);
            item.Parameters.AddWithValue("item_id", itemId);
            item.Parameters.AddWithValue("backend", (short)backend);
            await item.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var eventType = kind == HotCacheJobKind.Evict ? HotCacheEventType.Evicted : HotCacheEventType.Copied;
        await using var itemEvent = new NpgsqlCommand(
            """
            INSERT INTO hot_cache_events (
                item_id, backend, job_id, event_type, eviction_reason,
                bytes, duration_ms, occurred_at)
            VALUES (
                @item_id, @backend, @job_id, @event_type, @eviction_reason,
                @bytes, @duration_ms, @now)
            ON CONFLICT (job_id, event_type)
                WHERE job_id IS NOT NULL AND event_type IN (1, 2)
            DO NOTHING;
            """,
            connection,
            transaction);
        itemEvent.Parameters.AddWithValue("item_id", itemId);
        itemEvent.Parameters.AddWithValue("backend", (short)backend);
        itemEvent.Parameters.AddWithValue("job_id", acknowledgement.JobId);
        itemEvent.Parameters.AddWithValue("event_type", (short)eventType);
        itemEvent.Parameters.Add("eviction_reason", NpgsqlDbType.Smallint).Value = kind == HotCacheJobKind.Evict
            ? (short)(evictionReason ?? HotCacheEvictionReason.Manual)
            : DBNull.Value;
        itemEvent.Parameters.AddWithValue("bytes", acknowledgement.BytesProcessed);
        itemEvent.Parameters.AddWithValue("duration_ms", durationMilliseconds);
        itemEvent.Parameters.AddWithValue("now", now);
        await itemEvent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyFailedJobAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HotCacheJobAcknowledgement acknowledgement,
        HotCacheJobKind kind,
        Guid itemId,
        HotCacheBackend backend,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var retryable = acknowledgement.Outcome == HotCacheJobOutcome.RetryableFailure;
        var errorSummary = BoundError(acknowledgement.ErrorSummary);
        await using (var job = new NpgsqlCommand(
            """
            UPDATE hot_cache_jobs
               SET state = @job_state,
                   next_attempt_at = @next_attempt_at,
                   lease_owner = NULL,
                   lease_expires_at = NULL,
                   error_summary = @error_summary,
                   updated_at = @now
             WHERE job_id = @job_id;
            """,
            connection,
            transaction))
        {
            job.Parameters.AddWithValue("job_state", (short)(retryable ? 1 : 4));
            job.Parameters.AddWithValue("next_attempt_at", retryable ? now.AddSeconds(30) : now);
            job.Parameters.AddWithValue("error_summary", errorSummary);
            job.Parameters.AddWithValue("now", now);
            job.Parameters.AddWithValue("job_id", acknowledgement.JobId);
            await job.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var item = new NpgsqlCommand(
            """
            UPDATE hot_cache_items
               SET state = @item_state
             WHERE item_id = @item_id AND backend = @backend;
            """,
            connection,
            transaction))
        {
            item.Parameters.AddWithValue("item_state", (short)(retryable
                ? kind == HotCacheJobKind.Evict ? HotCacheItemState.Evicting : HotCacheItemState.Queued
                : HotCacheItemState.Failed));
            item.Parameters.AddWithValue("item_id", itemId);
            item.Parameters.AddWithValue("backend", (short)backend);
            await item.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var itemEvent = new NpgsqlCommand(
            """
            INSERT INTO hot_cache_events (
                item_id, backend, job_id, event_type, bytes,
                duration_ms, occurred_at, error_summary)
            VALUES (
                @item_id, @backend, @job_id, 3, @bytes,
                @duration_ms, @now, @error_summary);
            """,
            connection,
            transaction);
        itemEvent.Parameters.AddWithValue("item_id", itemId);
        itemEvent.Parameters.AddWithValue("backend", (short)backend);
        itemEvent.Parameters.AddWithValue("job_id", acknowledgement.JobId);
        itemEvent.Parameters.AddWithValue("bytes", acknowledgement.BytesProcessed);
        itemEvent.Parameters.AddWithValue("duration_ms", checked((long)acknowledgement.Duration.TotalMilliseconds));
        itemEvent.Parameters.AddWithValue("now", now);
        itemEvent.Parameters.AddWithValue("error_summary", errorSummary);
        await itemEvent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BoundError(string? errorSummary)
    {
        const int maximumErrorLength = 4096;
        if (string.IsNullOrWhiteSpace(errorSummary))
        {
            return "Unspecified worker failure.";
        }

        return errorSummary.Length <= maximumErrorLength
            ? errorSummary
            : errorSummary[..maximumErrorLength];
    }

    private static string? BoundErrorOrNull(string? errorSummary)
    {
        return string.IsNullOrWhiteSpace(errorSummary) ? null : BoundError(errorSummary);
    }

    private static HotCacheSettingsSnapshot ReadSettings(NpgsqlDataReader reader)
    {
        var configuredMaximumLookahead = reader.GetInt32(3);
        return new HotCacheSettingsSnapshot(
            reader.GetBoolean(0),
            (HotCacheBackend)reader.GetInt16(1),
            TimeSpan.FromDays(reader.GetInt32(2)),
            configuredMaximumLookahead,
            configuredMaximumLookahead,
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetInt64(6));
    }

    private static void ValidateSettings(HotCacheSettingsUpdate update)
    {
        if (!Enum.IsDefined(update.Backend))
        {
            throw new ArgumentOutOfRangeException(nameof(update), "Backend is not supported.");
        }

        if (update.ActivityWindow < TimeSpan.FromDays(1)
            || update.ActivityWindow > TimeSpan.FromDays(3650)
            || update.ActivityWindow.TotalDays % 1 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(update), "Activity window must contain 1 to 3650 whole days.");
        }

        if (update.ConfiguredMaximumLookahead is < 0 or > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(update), "Configured lookahead must be between 0 and 6.");
        }

        if (update.MinimumFreeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(update), "Minimum free bytes cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(update.ManagedDirectory)
            || update.ManagedDirectory.Length > 512
            || update.ManagedDirectory != update.ManagedDirectory.Trim()
            || Path.IsPathRooted(update.ManagedDirectory)
            || update.ManagedDirectory.Contains('\\', StringComparison.Ordinal)
            || update.ManagedDirectory.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Managed directory must be a safe relative path beneath the fixed mount root.", nameof(update));
        }
    }

    private static async Task<int> CountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A hot-cache count query returned no value."));
    }
}
