using System;
using System.Threading.Tasks;
using Emby.Server.Implementations.MediaEncoding;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.MediaEncoding;

/// <summary>
/// Exercises the production Redis scripts when a test endpoint is available.
/// </summary>
public sealed class RedisTranscodeSessionStoreIntegrationTests
{
    /// <summary>
    /// Verifies create, owner fencing, retained recovery state, takeover, and cleanup.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "IntegrationTest")]
    public async Task LeaseLifecycle_EnforcesSingleOwnerAndRetainsRecoveryCheckpoint()
    {
        var connectionString = Environment.GetEnvironmentVariable("JELLYFIN_TEST_REDIS");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set JELLYFIN_TEST_REDIS to run Redis integration tests.");

        await using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString).ConfigureAwait(false);
        var store = new RedisTranscodeSessionStore(
            redis,
            Options.Create(new TranscodeStoreOptions
            {
                LeaseDurationSeconds = 1,
                RecoveryRetentionSeconds = 30
            }),
            NullLogger<RedisTranscodeSessionStore>.Instance);

        var playSessionId = "integration-" + Guid.NewGuid().ToString("N");
        var initial = new TranscodeSession
        {
            PlaySessionId = playSessionId,
            OwnerPod = "node-a",
            MediaSourceId = "media-1",
            ManifestPath = "/cache/stream.m3u8",
            SegmentPathPrefix = "/cache/stream"
        };

        Assert.True(await store.TryCreateAsync(initial).ConfigureAwait(false));
        Assert.False(await store.TryCreateAsync(initial).ConfigureAwait(false));
        Assert.False(await store.RenewLeaseAsync(playSessionId, "node-b").ConfigureAwait(false));
        Assert.False(await store.UpdateProgressAsync(playSessionId, "node-b", "/bad", "/bad", 99, 99).ConfigureAwait(false));
        Assert.True(await store.UpdateProgressAsync(playSessionId, "node-a", initial.ManifestPath, initial.SegmentPathPrefix, 7, 1234).ConfigureAwait(false));

        await Task.Delay(TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false);

        var expired = await store.TryGetAsync(playSessionId).ConfigureAwait(false);
        Assert.NotNull(expired);
        Assert.Equal(7, expired.LastCompletedSegmentIndex);
        Assert.Equal(1234, expired.LastDurablePlaybackOffset);
        Assert.True(expired.LeaseExpiresUtc <= DateTime.UtcNow);

        Assert.True(await store.TryTakeoverAsync(playSessionId, "node-b").ConfigureAwait(false));
        Assert.False(await store.TryTakeoverAsync(playSessionId, "node-c").ConfigureAwait(false));
        Assert.False(await store.RenewLeaseAsync(playSessionId, "node-a").ConfigureAwait(false));
        Assert.False(await store.DeleteAsync(playSessionId, "node-a").ConfigureAwait(false));

        var recovered = await store.TryGetAsync(playSessionId).ConfigureAwait(false);
        Assert.NotNull(recovered);
        Assert.Equal("node-b", recovered.OwnerPod);
        Assert.Equal(7, recovered.LastCompletedSegmentIndex);

        Assert.True(await store.DeleteAsync(playSessionId, "node-b").ConfigureAwait(false));
        Assert.Null(await store.TryGetAsync(playSessionId).ConfigureAwait(false));
    }
}
