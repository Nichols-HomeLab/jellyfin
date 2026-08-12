using System;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

public class BaseItemQueryGenerationTests
{
    [Fact]
    public void SelectRepresentativeIdsUsesStableAggregate()
    {
        var firstGroupLowestId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var firstGroupHigherId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var secondGroupId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var items = new[]
        {
            new BaseItemEntity { Id = firstGroupHigherId, Type = "Episode", PresentationUniqueKey = "first" },
            new BaseItemEntity { Id = firstGroupLowestId, Type = "Episode", PresentationUniqueKey = "first" },
            new BaseItemEntity { Id = secondGroupId, Type = "Episode", PresentationUniqueKey = "second" }
        }.AsQueryable();

        var query = BaseItemRepository.SelectRepresentativeIds(items, item => item.PresentationUniqueKey);
        var result = query.Order().ToArray();
        var expression = query.Expression.ToString();

        Assert.Equal([firstGroupLowestId, secondGroupId], result);
        Assert.Contains("Min", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("FirstOrDefault", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectRepresentativeIdsPreservingIncompleteKeysDoesNotCollapseMissingKeys()
    {
        var keyedLowestId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var keyedHigherId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var nullKeyId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var emptyKeyId = Guid.Parse("00000000-0000-0000-0000-000000000004");
        var items = new[]
        {
            new BaseItemEntity { Id = keyedHigherId, Type = "Episode", PresentationUniqueKey = "same" },
            new BaseItemEntity { Id = keyedLowestId, Type = "Episode", PresentationUniqueKey = "same" },
            new BaseItemEntity { Id = nullKeyId, Type = "Episode", PresentationUniqueKey = null },
            new BaseItemEntity { Id = emptyKeyId, Type = "Episode", PresentationUniqueKey = string.Empty }
        }.AsQueryable();

        var result = BaseItemRepository.SelectRepresentativeIdsPreservingIncompleteKeys(
                items,
                item => item.PresentationUniqueKey != null && item.PresentationUniqueKey != string.Empty,
                item => item.PresentationUniqueKey)
            .Order()
            .ToArray();

        Assert.Equal([keyedLowestId, nullKeyId, emptyKeyId], result);
    }

    [Fact]
    public void MergeUserDataForPlaceholderPreservesStrongestWatchState()
    {
        var userId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var older = new UserData
        {
            ItemId = Guid.Parse("20000000-0000-0000-0000-000000000001"),
            Item = null,
            UserId = userId,
            User = null,
            CustomDataKey = "episode-key",
            Played = true,
            PlayCount = 3,
            IsFavorite = true,
            LastPlayedDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var newer = new UserData
        {
            ItemId = Guid.Parse("20000000-0000-0000-0000-000000000002"),
            Item = null,
            UserId = userId,
            User = null,
            CustomDataKey = "episode-key",
            Played = false,
            PlayCount = 1,
            PlaybackPositionTicks = 42,
            LastPlayedDate = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc)
        };
        var retentionDate = new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc);

        var result = BaseItemRepository.MergeUserDataForPlaceholder([older, newer], retentionDate);

        Assert.Equal(BaseItemRepository.PlaceholderId, result.ItemId);
        Assert.Equal(userId, result.UserId);
        Assert.Equal("episode-key", result.CustomDataKey);
        Assert.True(result.Played);
        Assert.Equal(3, result.PlayCount);
        Assert.True(result.IsFavorite);
        Assert.Equal(42, result.PlaybackPositionTicks);
        Assert.Equal(newer.LastPlayedDate, result.LastPlayedDate);
        Assert.Equal(retentionDate, result.RetentionDate);
    }
}
