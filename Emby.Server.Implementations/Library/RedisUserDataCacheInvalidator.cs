using System;
using System.Threading.Tasks;
using Emby.Server.Implementations.MediaEncoding;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Emby.Server.Implementations.Library;

/// <summary>
/// Distributes user-data cache invalidations over Redis pub/sub.
/// </summary>
public sealed class RedisUserDataCacheInvalidator : IUserDataCacheInvalidator, IDisposable
{
    private const string ChannelName = "jellyfin:user-data-cache:v1";
    private readonly RedisConnectionManager _redis;
    private readonly ILogger<RedisUserDataCacheInvalidator> _logger;
    private readonly string _source = Guid.NewGuid().ToString("N");
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisUserDataCacheInvalidator"/> class.
    /// </summary>
    /// <param name="redis">The shared Redis connection.</param>
    /// <param name="logger">The logger.</param>
    public RedisUserDataCacheInvalidator(
        RedisConnectionManager redis,
        ILogger<RedisUserDataCacheInvalidator> logger)
    {
        _redis = redis;
        _logger = logger;
        _redis.ConnectionReplaced += OnConnectionReplaced;
        _redis.ExecuteAsync(SubscribeAsync).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public event Action<string>? Invalidated;

    /// <inheritdoc />
    public void Publish(string cacheKey)
    {
        try
        {
            var message = _source + '|' + cacheKey;
            _redis.ExecuteAsync(connection => connection.GetSubscriber().PublishAsync(
                    RedisChannel.Literal(ChannelName),
                    message))
                .GetAwaiter()
                .GetResult();
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Failed to publish a user-data cache invalidation.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _redis.ConnectionReplaced -= OnConnectionReplaced;
        GC.SuppressFinalize(this);
    }

    private async Task<bool> SubscribeAsync(IConnectionMultiplexer connection)
    {
        await connection.GetSubscriber().SubscribeAsync(
                RedisChannel.Literal(ChannelName),
                (_, value) => HandleMessage(value))
            .ConfigureAwait(false);
        return true;
    }

    private void OnConnectionReplaced(IConnectionMultiplexer connection)
    {
        try
        {
            SubscribeAsync(connection).GetAwaiter().GetResult();
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Failed to restore the user-data invalidation subscription after reconnecting Redis.");
        }
    }

    private void HandleMessage(RedisValue value)
    {
        var message = value.ToString();
        var separator = message.IndexOf('|', StringComparison.Ordinal);
        if (separator <= 0 || message.AsSpan(0, separator).SequenceEqual(_source.AsSpan()))
        {
            return;
        }

        var cacheKey = message[(separator + 1)..];
        if (!string.IsNullOrEmpty(cacheKey))
        {
            Invalidated?.Invoke(cacheKey);
        }
    }
}
