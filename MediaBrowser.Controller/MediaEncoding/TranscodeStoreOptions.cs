namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Configuration options for the transcode session store.
/// </summary>
public sealed class TranscodeStoreOptions
{
    /// <summary>
    /// Gets or sets the Redis connection string.
    /// A <c>null</c> or empty value indicates single-instance mode, where
    /// <see cref="NullTranscodeSessionStore"/> is used instead of a Redis-backed store.
    /// </summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>
    /// Gets or sets the duration in seconds for which a transcoding session lease is valid.
    /// </summary>
    public int LeaseDurationSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets how long an expired session remains available for recovery.
    /// This must be longer than <see cref="LeaseDurationSeconds"/> so another
    /// instance can inspect and atomically claim the durable checkpoint.
    /// </summary>
    public int RecoveryRetentionSeconds { get; set; } = 300;
}
