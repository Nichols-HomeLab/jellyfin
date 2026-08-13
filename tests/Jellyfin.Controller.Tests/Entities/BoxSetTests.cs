using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Controller.Tests.Entities;

public class BoxSetTests
{
    [Fact]
    public void SupportsUserDataFromChildren_IsDisabled()
    {
        var boxSet = new BoxSet();

        Assert.False(boxSet.SupportsUserDataFromChildren);
    }
}
