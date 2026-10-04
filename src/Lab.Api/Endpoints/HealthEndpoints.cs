using DataPerformanceLab.Caching;
using DataPerformanceLab.Data;
using Microsoft.EntityFrameworkCore;

namespace DataPerformanceLab.Api.Endpoints;

public static class HealthEndpoints
{
    private sealed record ReadyResponse(string Status, string Sql, string Cache);

    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

        // SQL is required. Redis being unreachable is reported as cache degradation,
        // but an API that can still serve every request from SQL stays ready.
        app.MapGet("/health/ready", async Task<IResult> (
            LabDbContext db,
            IProductCache cache,
            CancellationToken cancellationToken) =>
        {
            var sqlReachable = await db.Database.CanConnectAsync(cancellationToken);
            if (!sqlReachable)
            {
                return Results.Json(
                    new ReadyResponse("not-ready", "unreachable", "unknown"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var cacheStatus = !cache.Enabled
                ? "disabled"
                : await cache.IsReachableAsync(cancellationToken) ? "reachable" : "degraded";

            return Results.Ok(new ReadyResponse("ready", "reachable", cacheStatus));
        });
    }
}
