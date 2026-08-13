using System;
using System.Collections.Generic;
using Emby.Server.Implementations.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class UserDataCacheTests
{
    [Fact]
    public void FallbackRegistrationDoesNotReplaceHaInvalidator()
    {
        var services = new ServiceCollection();
        var invalidator = new FakeInvalidator(new FakeInvalidationHub());
        services.AddSingleton<IUserDataCacheInvalidator>(invalidator);

        services.AddUserDataCacheInvalidatorFallback();

        using var provider = services.BuildServiceProvider();
        Assert.Same(invalidator, provider.GetRequiredService<IUserDataCacheInvalidator>());
    }

    [Fact]
    public void PublishInvalidationEvictsOnlyRemoteReplicaCache()
    {
        var hub = new FakeInvalidationHub();
        var replicaAInvalidator = hub.CreateInvalidator();
        var replicaBInvalidator = hub.CreateInvalidator();
        using var replicaA = new UserDataCache(10, replicaAInvalidator);
        using var replicaB = new UserDataCache(10, replicaBInvalidator);
        const string CacheKey = "1-00000000000000000000000000000001";

        replicaA.AddOrUpdate(CacheKey, new UserItemData { Key = CacheKey, PlayCount = 1 });
        replicaB.AddOrUpdate(CacheKey, new UserItemData { Key = CacheKey, PlayCount = 1 });

        replicaA.AddOrUpdate(CacheKey, new UserItemData { Key = CacheKey, PlayCount = 2 });
        replicaA.PublishInvalidation(CacheKey);

        Assert.True(replicaA.TryGet(CacheKey, out var replicaAValue));
        Assert.Equal(2, replicaAValue.PlayCount);
        Assert.False(replicaB.TryGet(CacheKey, out _));
    }

    private sealed class FakeInvalidationHub
    {
        private readonly List<FakeInvalidator> _invalidators = [];

        public FakeInvalidator CreateInvalidator()
        {
            var invalidator = new FakeInvalidator(this);
            _invalidators.Add(invalidator);
            return invalidator;
        }

        public void Publish(FakeInvalidator source, string cacheKey)
        {
            foreach (var invalidator in _invalidators)
            {
                if (!ReferenceEquals(source, invalidator))
                {
                    invalidator.Receive(cacheKey);
                }
            }
        }
    }

    private sealed class FakeInvalidator(FakeInvalidationHub hub) : IUserDataCacheInvalidator
    {
        public event Action<string>? Invalidated;

        public void Publish(string cacheKey)
            => hub.Publish(this, cacheKey);

        public void Receive(string cacheKey)
            => Invalidated?.Invoke(cacheKey);
    }
}
