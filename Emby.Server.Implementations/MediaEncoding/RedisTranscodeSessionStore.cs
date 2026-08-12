using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Emby.Server.Implementations.MediaEncoding;

/// <summary>
/// A Redis-backed implementation of <see cref="ITranscodeSessionStore"/> that provides
/// durable, distributed session tracking with lease-based ownership between pods.
/// </summary>
public sealed class RedisTranscodeSessionStore : ITranscodeSessionStore
{
    private const string KeyPrefix = "jellyfin:transcode:";
    private const string LiveStreamKeyPrefix = "jellyfin:livestream:";

    /// <summary>
    /// Lua script for atomic takeover: reads the stored session, checks its numeric Unix-time
    /// lease expiry, and if expired updates the owner and expiry before returning 1.
    /// </summary>
    private const string TakeoverScript = @"
local raw = redis.call('GET', KEYS[1])
if not raw then return 0 end
local session = cjson.decode(raw)
local currentMs = tonumber(ARGV[1])
if tonumber(session['LeaseExpiresUnixTimeMilliseconds'] or 0) > currentMs then return 0 end
session['OwnerPod'] = ARGV[2]
local leaseDurationMs = tonumber(ARGV[3])
session['LeaseExpiresUnixTimeMilliseconds'] = currentMs + leaseDurationMs
redis.call('SET', KEYS[1], cjson.encode(session), 'PX', tonumber(ARGV[4]))
return 1";

    private const string RenewScript = @"
local raw = redis.call('GET', KEYS[1])
if not raw then return 0 end
local session = cjson.decode(raw)
local currentMs = tonumber(ARGV[2])
if session['OwnerPod'] ~= ARGV[1] then return 0 end
if tonumber(session['LeaseExpiresUnixTimeMilliseconds'] or 0) <= currentMs then return 0 end
session['LeaseExpiresUnixTimeMilliseconds'] = currentMs + tonumber(ARGV[3])
redis.call('SET', KEYS[1], cjson.encode(session), 'PX', tonumber(ARGV[4]))
return 1";

    private const string DeleteIfOwnerScript = @"
local raw = redis.call('GET', KEYS[1])
if not raw then return 0 end
local session = cjson.decode(raw)
if session['OwnerPod'] ~= ARGV[1] then return 0 end
return redis.call('DEL', KEYS[1])";

    private const string UpdateProgressScript = @"
local raw = redis.call('GET', KEYS[1])
if not raw then return 0 end
local session = cjson.decode(raw)
if session['OwnerPod'] ~= ARGV[1] then return 0 end
local segmentIndex = tonumber(ARGV[4])
local playbackOffset = tonumber(ARGV[5])
if segmentIndex >= tonumber(session['LastCompletedSegmentIndex'] or -1) then
  session['LastCompletedSegmentIndex'] = segmentIndex
end
if playbackOffset >= tonumber(session['LastDurablePlaybackOffset'] or 0) then
  session['LastDurablePlaybackOffset'] = playbackOffset
end
session['ManifestPath'] = ARGV[2]
session['SegmentPathPrefix'] = ARGV[3]
redis.call('SET', KEYS[1], cjson.encode(session), 'PX', tonumber(ARGV[6]))
return 1";

    private readonly RedisConnectionManager _redis;
    private readonly TranscodeStoreOptions _options;
    private readonly ILogger<RedisTranscodeSessionStore> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisTranscodeSessionStore"/> class.
    /// </summary>
    /// <param name="redis">The managed Redis connection.</param>
    /// <param name="options">The transcode store configuration options.</param>
    /// <param name="logger">The logger.</param>
    public RedisTranscodeSessionStore(
        RedisConnectionManager redis,
        IOptions<TranscodeStoreOptions> options,
        ILogger<RedisTranscodeSessionStore> logger)
    {
        _redis = redis;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public async Task SetAsync(TranscodeSession session, CancellationToken cancellationToken = default)
    {
        SetLeaseExpiry(session);
        var key = GetKey(session.PlaySessionId);
        var json = JsonSerializer.Serialize(session);
        await ExecuteAsync(db => db.StringSetAsync(key, json, GetRecoveryRetention()), cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Set transcode session {PlaySessionId} in Redis.", session.PlaySessionId.ReplaceLineEndings(string.Empty));
    }

    /// <inheritdoc />
    public async Task<bool> TryCreateAsync(TranscodeSession session, CancellationToken cancellationToken = default)
    {
        SetLeaseExpiry(session);
        var created = await ExecuteAsync(
            db => db.StringSetAsync(
                GetKey(session.PlaySessionId),
                JsonSerializer.Serialize(session),
                GetRecoveryRetention(),
                When.NotExists),
            cancellationToken).ConfigureAwait(false);
        return created;
    }

    /// <inheritdoc />
    public async Task<TranscodeSession?> TryGetAsync(string playSessionId, CancellationToken cancellationToken = default)
    {
        var key = GetKey(playSessionId);
        var raw = await ExecuteAsync(db => db.StringGetAsync(key), cancellationToken).ConfigureAwait(false);
        if (!raw.HasValue)
        {
            return null;
        }

        var session = JsonSerializer.Deserialize<TranscodeSession>(raw.ToString());
        NormalizeLeaseExpiry(session);
        return session;
    }

    /// <inheritdoc />
    public async Task RenewLeaseAsync(string playSessionId, CancellationToken cancellationToken = default)
    {
        var key = GetKey(playSessionId);
        var raw = await ExecuteAsync(db => db.StringGetAsync(key), cancellationToken).ConfigureAwait(false);
        if (!raw.HasValue)
        {
            return;
        }

        var session = JsonSerializer.Deserialize<TranscodeSession>(raw.ToString());
        if (session is null)
        {
            return;
        }

        SetLeaseExpiry(session);
        var json = JsonSerializer.Serialize(session);
        await ExecuteAsync(db => db.StringSetAsync(key, json, GetRecoveryRetention()), cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Renewed lease for transcode session {PlaySessionId}.", playSessionId.ReplaceLineEndings(string.Empty));
    }

    /// <inheritdoc />
    public async Task<bool> RenewLeaseAsync(string playSessionId, string ownerPod, CancellationToken cancellationToken = default)
    {
        var result = (long?)await ExecuteAsync(
            db => db.ScriptEvaluateAsync(
                RenewScript,
                new RedisKey[] { GetKey(playSessionId) },
                new RedisValue[]
                {
                    ownerPod,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    GetLeaseDurationMilliseconds(),
                    (long)GetRecoveryRetention().TotalMilliseconds
                }),
            cancellationToken).ConfigureAwait(false);
        return result == 1;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string playSessionId, CancellationToken cancellationToken = default)
    {
        var key = GetKey(playSessionId);
        await ExecuteAsync(db => db.KeyDeleteAsync(key), cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Deleted transcode session {PlaySessionId} from Redis.", playSessionId.ReplaceLineEndings(string.Empty));
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string playSessionId, string ownerPod, CancellationToken cancellationToken = default)
    {
        var result = (long?)await ExecuteAsync(
            db => db.ScriptEvaluateAsync(
                DeleteIfOwnerScript,
                new RedisKey[] { GetKey(playSessionId) },
                new RedisValue[] { ownerPod }),
            cancellationToken).ConfigureAwait(false);
        return result == 1;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateProgressAsync(
        string playSessionId,
        string ownerPod,
        string manifestPath,
        string segmentPathPrefix,
        int completedSegmentIndex,
        long durablePlaybackOffset,
        CancellationToken cancellationToken = default)
    {
        var result = (long?)await ExecuteAsync(
            db => db.ScriptEvaluateAsync(
                UpdateProgressScript,
                new RedisKey[] { GetKey(playSessionId) },
                new RedisValue[]
                {
                    ownerPod,
                    manifestPath,
                    segmentPathPrefix,
                    completedSegmentIndex,
                    durablePlaybackOffset,
                    (long)GetRecoveryRetention().TotalMilliseconds
                }),
            cancellationToken).ConfigureAwait(false);
        return result == 1;
    }

    /// <inheritdoc />
    public async Task<bool> TryTakeoverAsync(string playSessionId, string claimingPod, CancellationToken cancellationToken = default)
    {
        var key = GetKey(playSessionId);
        var leaseDurationMs = (long)_options.LeaseDurationSeconds * 1000;
        var currentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var result = (long?)await ExecuteAsync(
            db => db.ScriptEvaluateAsync(
                TakeoverScript,
                keys: new RedisKey[] { key },
                values: new RedisValue[]
                {
                    currentMs,
                    claimingPod,
                    leaseDurationMs,
                    (long)GetRecoveryRetention().TotalMilliseconds
                }),
            cancellationToken).ConfigureAwait(false);

        var succeeded = result == 1;
        if (succeeded)
        {
            _logger.LogInformation(
                "Pod {ClaimingPod} successfully took over transcode session {PlaySessionId}.",
                claimingPod.ReplaceLineEndings(string.Empty),
                playSessionId.ReplaceLineEndings(string.Empty));
        }

        return succeeded;
    }

    private static string GetKey(string playSessionId) => KeyPrefix + playSessionId;

    private static string GetLiveStreamKey(string liveStreamId, string sessionIdOrPlaySessionId)
        => LiveStreamKeyPrefix + liveStreamId + ":" + sessionIdOrPlaySessionId;

    /// <inheritdoc />
    public async Task<IEnumerable<TranscodeSession>> GetActiveSessionsAsync(CancellationToken cancellationToken = default)
    {
        return await _redis.ExecuteAsync<IEnumerable<TranscodeSession>>(
            async redis =>
            {
                var sessions = new List<TranscodeSession>();
                var db = redis.GetDatabase();
                foreach (var server in redis.GetServers())
                {
                    if (!server.IsConnected)
                    {
                        continue;
                    }

                    var keys = new List<RedisKey>();
                    await foreach (var key in server.KeysAsync(database: db.Database, pattern: KeyPrefix + "*", pageSize: 1000).WithCancellation(cancellationToken).ConfigureAwait(false))
                    {
                        keys.Add(key);
                    }

                    var tasks = keys.Select(key => db.StringGetAsync(key)).ToList();
                    var values = await Task.WhenAll(tasks).ConfigureAwait(false);

                    foreach (var raw in values)
                    {
                        if (!raw.HasValue)
                        {
                            continue;
                        }

                        TranscodeSession? session;
                        try
                        {
                            session = JsonSerializer.Deserialize<TranscodeSession>(raw.ToString());
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to deserialize transcode session from Redis.");
                            continue;
                        }

                        NormalizeLeaseExpiry(session);
                        if (session is not null && session.LeaseExpiresUtc > DateTime.UtcNow)
                        {
                            sessions.Add(session);
                        }
                    }
                }

                return sessions;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetLiveStreamAsync(LiveStreamSession session, CancellationToken cancellationToken = default)
    {
        var key = GetLiveStreamKey(session.LiveStreamId, session.SessionId);
        var json = JsonSerializer.Serialize(session);
        // Live stream records use the same lease duration as transcode sessions.
        var leaseDurationMs = (long)_options.LeaseDurationSeconds * 1000;
        await _redis.ExecuteAsync(
            async redis =>
            {
                var db = redis.GetDatabase();
                await db.StringSetAsync(key, json, TimeSpan.FromMilliseconds(leaseDurationMs)).ConfigureAwait(false);

                // Also index by play session id so the caller can look up by either key.
                if (!string.IsNullOrEmpty(session.PlaySessionId))
                {
                    var playKey = GetLiveStreamKey(session.LiveStreamId, session.PlaySessionId);
                    await db.StringSetAsync(playKey, json, TimeSpan.FromMilliseconds(leaseDurationMs)).ConfigureAwait(false);
                }

                return true;
            },
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Set live stream session {LiveStreamId}/{SessionId} in Redis.",
            session.LiveStreamId.ReplaceLineEndings(string.Empty),
            session.SessionId.ReplaceLineEndings(string.Empty));
    }

    /// <inheritdoc />
    public async Task<LiveStreamSession?> TryGetLiveStreamAsync(string liveStreamId, string sessionIdOrPlaySessionId, CancellationToken cancellationToken = default)
    {
        var key = GetLiveStreamKey(liveStreamId, sessionIdOrPlaySessionId);
        var raw = await ExecuteAsync(db => db.StringGetAsync(key), cancellationToken).ConfigureAwait(false);
        if (!raw.HasValue)
        {
            return null;
        }

        return JsonSerializer.Deserialize<LiveStreamSession>(raw.ToString());
    }

    /// <inheritdoc />
    public async Task DeleteLiveStreamAsync(string liveStreamId, string sessionIdOrPlaySessionId, CancellationToken cancellationToken = default)
    {
        var key = GetLiveStreamKey(liveStreamId, sessionIdOrPlaySessionId);
        var raw = await ExecuteAsync(db => db.StringGetAsync(key), cancellationToken).ConfigureAwait(false);
        if (raw.HasValue)
        {
            var session = JsonSerializer.Deserialize<LiveStreamSession>(raw.ToString());
            if (session is not null)
            {
                // Remove both the session-id key and the play-session-id key if present.
                var keysToDelete = new System.Collections.Generic.List<RedisKey>
                {
                    GetLiveStreamKey(liveStreamId, session.SessionId)
                };

                if (!string.IsNullOrEmpty(session.PlaySessionId))
                {
                    keysToDelete.Add(GetLiveStreamKey(liveStreamId, session.PlaySessionId));
                }

                await ExecuteAsync(db => db.KeyDeleteAsync(keysToDelete.ToArray()), cancellationToken).ConfigureAwait(false);
                _logger.LogDebug(
                    "Deleted live stream session {LiveStreamId}/{SessionId} from Redis.",
                    liveStreamId.ReplaceLineEndings(string.Empty),
                    session.SessionId.ReplaceLineEndings(string.Empty));
                return;
            }
        }

        // Fallback: delete just the key that was supplied.
        await ExecuteAsync(db => db.KeyDeleteAsync(key), cancellationToken).ConfigureAwait(false);
    }

    private Task<T> ExecuteAsync<T>(
        Func<IDatabase, Task<T>> operation,
        CancellationToken cancellationToken)
        => _redis.ExecuteAsync(redis => operation(redis.GetDatabase()), cancellationToken);

    private long GetLeaseDurationMilliseconds()
        => Math.Max(1, _options.LeaseDurationSeconds) * 1000L;

    private TimeSpan GetRecoveryRetention()
        => TimeSpan.FromSeconds(Math.Max(
            _options.RecoveryRetentionSeconds,
            Math.Max(1, _options.LeaseDurationSeconds) * 2));

    private void SetLeaseExpiry(TranscodeSession session)
    {
        session.LeaseExpiresUtc = DateTime.UtcNow.AddMilliseconds(GetLeaseDurationMilliseconds());
        session.LeaseExpiresUnixTimeMilliseconds = new DateTimeOffset(session.LeaseExpiresUtc).ToUnixTimeMilliseconds();
    }

    private static void NormalizeLeaseExpiry(TranscodeSession? session)
    {
        if (session is not null && session.LeaseExpiresUnixTimeMilliseconds > 0)
        {
            session.LeaseExpiresUtc = DateTimeOffset
                .FromUnixTimeMilliseconds(session.LeaseExpiresUnixTimeMilliseconds)
                .UtcDateTime;
        }
    }
}
