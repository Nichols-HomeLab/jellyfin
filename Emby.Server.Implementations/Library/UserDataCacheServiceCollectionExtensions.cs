using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Emby.Server.Implementations.Library;

internal static class UserDataCacheServiceCollectionExtensions
{
    public static void AddUserDataCacheInvalidatorFallback(this IServiceCollection services)
        => services.TryAddSingleton<IUserDataCacheInvalidator, NullUserDataCacheInvalidator>();
}
