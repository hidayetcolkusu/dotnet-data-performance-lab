using DataPerformanceLab.Caching;
using DataPerformanceLab.Catalog;
using DataPerformanceLab.Configuration;
using DataPerformanceLab.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DataPerformanceLab;

public static class LabCoreServiceCollectionExtensions
{
    public static IServiceCollection AddLabCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LabDataOptions>()
            .Bind(configuration.GetSection(LabDataOptions.SectionName));

        services.AddOptions<CacheOptions>()
            .Bind(configuration.GetSection(CacheOptions.SectionName));

        services.AddDbContext<LabDbContext>((sp, options) =>
        {
            var labOptions = sp.GetRequiredService<IOptions<LabDataOptions>>().Value;
            options.UseSqlServer(labOptions.SqlServer.GetConnectionString(labOptions.DatabaseName));
        });

        services.AddSingleton<ILabDbContextFactory, LabDbContextFactory>();
        services.AddSingleton<CatalogSeeder>();

        services.AddScoped<ProductQueries>();
        services.AddScoped<UpdatePrice>();

        // Counters and the Redis multiplexer are process-wide; opening a connection per
        // request would defeat the point of multiplexing.
        services.AddSingleton<CacheMetrics>();
        services.AddSingleton<IProductCacheConnection, ProductCacheConnection>();

        // Selecting the disabled implementation up front is what makes "cache off" provable:
        // with Cache:Enabled false there is no Redis client in the graph to accidentally call.
        services.AddSingleton<IProductCache>(sp =>
            sp.GetRequiredService<IOptions<CacheOptions>>().Value.Enabled
                ? ActivatorUtilities.CreateInstance<RedisProductCache>(sp)
                : new DisabledProductCache());

        services.AddScoped<CachedProductReader>();

        return services;
    }
}
