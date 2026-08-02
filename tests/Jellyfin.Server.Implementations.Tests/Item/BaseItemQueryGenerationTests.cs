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
}
