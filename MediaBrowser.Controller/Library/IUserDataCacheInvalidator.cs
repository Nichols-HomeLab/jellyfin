using System;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Distributes user-data cache invalidations between server instances.
/// </summary>
public interface IUserDataCacheInvalidator
{
    /// <summary>
    /// Occurs when another server instance changes a cache entry.
    /// </summary>
    event Action<string>? Invalidated;

    /// <summary>
    /// Notifies other server instances that a cache entry changed.
    /// </summary>
    /// <param name="cacheKey">The cache key.</param>
    void Publish(string cacheKey);
}
