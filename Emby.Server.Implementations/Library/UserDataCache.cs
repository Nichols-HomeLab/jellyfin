using System;
using BitFaster.Caching.Lru;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Emby.Server.Implementations.Library;

/// <summary>
/// Holds local user data and evicts entries changed by another server instance.
/// </summary>
internal sealed class UserDataCache : IDisposable
{
    private readonly FastConcurrentLru<string, UserItemData> _cache;
    private readonly IUserDataCacheInvalidator _invalidator;

    public UserDataCache(int capacity, IUserDataCacheInvalidator invalidator)
    {
        _cache = new FastConcurrentLru<string, UserItemData>(Environment.ProcessorCount, capacity, StringComparer.OrdinalIgnoreCase);
        _invalidator = invalidator;
        _invalidator.Invalidated += OnInvalidated;
    }

    public bool TryGet(string cacheKey, out UserItemData userData)
        => _cache.TryGet(cacheKey, out userData!);

    public UserItemData GetOrAdd(string cacheKey, Func<string, UserItemData> valueFactory)
        => _cache.GetOrAdd(cacheKey, valueFactory);

    public UserItemData GetOrAdd<T>(string cacheKey, Func<string, T, UserItemData> valueFactory, T factoryArgument)
        => _cache.GetOrAdd(cacheKey, valueFactory, factoryArgument);

    public void AddOrUpdate(string cacheKey, UserItemData userData)
        => _cache.AddOrUpdate(cacheKey, userData);

    public void PublishInvalidation(string cacheKey)
        => _invalidator.Publish(cacheKey);

    public void Dispose()
    {
        _invalidator.Invalidated -= OnInvalidated;
    }

    private void OnInvalidated(string cacheKey)
        => _cache.TryRemove(cacheKey, out _);
}
