using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Emby.Server.Implementations.MediaEncoding;

/// <summary>
/// Owns the Redis connection used by the transcode store and replaces it when
/// a Kubernetes Service connection remains attached to a demoted replica.
/// </summary>
public sealed class RedisConnectionManager : IDisposable
{
    private readonly Func<IConnectionMultiplexer> _connectionFactory;
    private readonly ILogger<RedisConnectionManager> _logger;
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private IConnectionMultiplexer _connection;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisConnectionManager"/> class.
    /// </summary>
    /// <param name="connectionFactory">Creates a fresh Redis connection.</param>
    /// <param name="logger">The logger.</param>
    public RedisConnectionManager(
        Func<IConnectionMultiplexer> connectionFactory,
        ILogger<RedisConnectionManager> logger)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
        _logger = logger;
        _connection = connectionFactory();
    }

    /// <summary>
    /// Executes an operation and retries it once on a new connection when the
    /// current connection reports that its server is no longer writable.
    /// </summary>
    /// <typeparam name="T">The operation result type.</typeparam>
    /// <param name="operation">The Redis operation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The operation result.</returns>
    public async Task<T> ExecuteAsync<T>(
        Func<IConnectionMultiplexer, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(operation);

        var observedConnection = Volatile.Read(ref _connection);
        try
        {
            return await operation(observedConnection).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsWritableEndpointFailure(ex))
        {
            _logger.LogWarning(
                ex,
                "Redis connection is attached to a non-writable endpoint; reconnecting and retrying once.");
            await ReconnectAsync(observedConnection, cancellationToken).ConfigureAwait(false);
            return await operation(Volatile.Read(ref _connection)).ConfigureAwait(false);
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
        Volatile.Read(ref _connection).Dispose();
        _reconnectLock.Dispose();
        GC.SuppressFinalize(this);
    }

    internal static bool IsWritableEndpointFailure(Exception exception)
    {
        if (exception is RedisServerException
            && exception.Message.StartsWith("READONLY", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (exception is RedisConnectionException
            && exception.Message.Contains("requires writable", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return exception.InnerException is not null
            && IsWritableEndpointFailure(exception.InnerException);
    }

    private async Task ReconnectAsync(
        IConnectionMultiplexer observedConnection,
        CancellationToken cancellationToken)
    {
        await _reconnectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(observedConnection, Volatile.Read(ref _connection)))
            {
                return;
            }

            var replacement = _connectionFactory();
            var previous = Interlocked.Exchange(ref _connection, replacement);
            previous.Dispose();
            _logger.LogInformation("Reconnected the Redis transcode store to a fresh writable endpoint.");
        }
        finally
        {
            _reconnectLock.Release();
        }
    }
}
