using System;
using MediaBrowser.Controller.Library;

namespace Emby.Server.Implementations.Library;

/// <summary>
/// No-op invalidator used when distributed caching is not configured.
/// </summary>
public sealed class NullUserDataCacheInvalidator : IUserDataCacheInvalidator
{
    /// <inheritdoc />
    public event Action<string>? Invalidated
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public void Publish(string cacheKey)
    {
    }
}
