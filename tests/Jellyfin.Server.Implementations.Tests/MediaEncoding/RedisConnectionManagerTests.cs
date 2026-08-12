using System;
using System.Threading.Tasks;
using Emby.Server.Implementations.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.MediaEncoding;

/// <summary>
/// Tests Redis connection replacement after a Service endpoint is demoted.
/// </summary>
public sealed class RedisConnectionManagerTests
{
    /// <summary>
    /// Replaces the observed connection and retries once on a writable endpoint failure.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task ExecuteAsync_RequiresWritable_ReconnectsAndRetriesOnce()
    {
        var first = new Mock<IConnectionMultiplexer>();
        var second = new Mock<IConnectionMultiplexer>();
        var factoryCalls = 0;
        using var manager = new RedisConnectionManager(
            () => ++factoryCalls == 1 ? first.Object : second.Object,
            NullLogger<RedisConnectionManager>.Instance);

        var result = await manager.ExecuteAsync(connection =>
        {
            if (ReferenceEquals(connection, first.Object))
            {
                throw new RedisServerException("READONLY You can't write against a read only replica.");
            }

            return Task.FromResult(42);
        }).ConfigureAwait(false);

        Assert.Equal(42, result);
        Assert.Equal(2, factoryCalls);
        first.Verify(connection => connection.Dispose(), Times.Once);
    }

    /// <summary>
    /// Does not replace a healthy connection for unrelated application failures.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task ExecuteAsync_UnrelatedFailure_DoesNotReconnect()
    {
        var connection = new Mock<IConnectionMultiplexer>();
        var factoryCalls = 0;
        using var manager = new RedisConnectionManager(
            () =>
            {
                factoryCalls++;
                return connection.Object;
            },
            NullLogger<RedisConnectionManager>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.ExecuteAsync<int>(_ => throw new InvalidOperationException("application failure"))).ConfigureAwait(false);

        Assert.Equal(1, factoryCalls);
    }

    /// <summary>
    /// Recognizes a writable-endpoint failure wrapped by an async Redis operation.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task ExecuteAsync_WrappedReadOnlyFailure_ReconnectsAndRetriesOnce()
    {
        var first = new Mock<IConnectionMultiplexer>();
        var second = new Mock<IConnectionMultiplexer>();
        var factoryCalls = 0;
        using var manager = new RedisConnectionManager(
            () => ++factoryCalls == 1 ? first.Object : second.Object,
            NullLogger<RedisConnectionManager>.Instance);

        var result = await manager.ExecuteAsync(connection =>
        {
            if (ReferenceEquals(connection, first.Object))
            {
                throw new InvalidOperationException(
                    "Redis operation failed.",
                    new RedisServerException("READONLY You can't write against a read only replica."));
            }

            return Task.FromResult(84);
        }).ConfigureAwait(false);

        Assert.Equal(84, result);
        Assert.Equal(2, factoryCalls);
        first.Verify(connection => connection.Dispose(), Times.Once);
    }
}
