using System.Data.Common;
using DataPerformanceLab.Api.Errors;
using DataPerformanceLab.Caching;
using DataPerformanceLab.Catalog;

namespace DataPerformanceLab.Api.Endpoints;

public static class ProductEndpoints
{
    private sealed record ProductListResponse(IReadOnlyList<ProductListItem> Items, string? NextCursor);

    /// <summary><c>expectedVersion</c> is the base64 <c>Version</c> from a previous read.</summary>
    public sealed record UpdatePriceRequest(decimal UnitPrice, string? ExpectedVersion);

    public static void MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/products").WithTags("products");

        // Keyset-paginated list. Never cached: the cache holds product detail only.
        group.MapGet("/", async Task<IResult> (
            int categoryId,
            int? pageSize,
            string? cursor,
            ProductQueries queries,
            HttpContext httpContext,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (categoryId is < 1 or > 20)
            {
                return Invalid(httpContext, "InvalidCategory", "categoryId must be between 1 and 20.");
            }

            var size = pageSize ?? 50;
            if (size is < 1 or > ProductQueries.MaxPageSize)
            {
                return Invalid(
                    httpContext,
                    "InvalidPageSize",
                    $"pageSize must be between 1 and {ProductQueries.MaxPageSize}.");
            }

            ProductCursor? parsedCursor = null;
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                if (!ProductCursor.TryParse(cursor, categoryId, out parsedCursor, out var cursorError))
                {
                    return Invalid(httpContext, "InvalidCursor", cursorError!);
                }
            }

            try
            {
                var page = await queries.ListAsync(categoryId, size, parsedCursor, cancellationToken);
                return TypedResults.Ok(new ProductListResponse(page.Items, page.NextCursor?.Encode()));
            }
            catch (DbException ex)
            {
                loggerFactory.CreateLogger("ProductEndpoints")
                    .LogError(ex, "SQL failure while listing products (category {CategoryId})", categoryId);
                return Unavailable(httpContext);
            }
        });

        // Product detail: the cache-aside endpoint the k6 comparison drives.
        group.MapGet("/{sku}", async Task<IResult> (
            string sku,
            CachedProductReader reader,
            HttpContext httpContext,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var normalized = SkuValidator.Normalize(sku);
            if (!SkuValidator.IsValid(normalized))
            {
                return Invalid(httpContext, "InvalidSku", InvalidSkuDetail);
            }

            try
            {
                var detail = await reader.ReadAsync(normalized, cancellationToken);
                return detail is null ? NotFound(httpContext, normalized) : TypedResults.Ok(detail);
            }
            catch (DbException ex)
            {
                loggerFactory.CreateLogger("ProductEndpoints")
                    .LogError(ex, "SQL failure while reading product {Sku}", normalized);
                return Unavailable(httpContext);
            }
        });

        // Price update with optimistic concurrency, then cache invalidation.
        group.MapPut("/{sku}/price", async Task<IResult> (
            string sku,
            UpdatePriceRequest request,
            UpdatePrice updatePrice,
            HttpContext httpContext,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var normalized = SkuValidator.Normalize(sku);
            if (!SkuValidator.IsValid(normalized))
            {
                return Invalid(httpContext, "InvalidSku", InvalidSkuDetail);
            }

            if (!UpdatePrice.IsValidPrice(request.UnitPrice))
            {
                return Invalid(
                    httpContext,
                    "InvalidUnitPrice",
                    "unitPrice must be between 0 and 9999999999999999.99 with at most two decimals.");
            }

            if (!TryDecodeVersion(request.ExpectedVersion, out var expectedVersion))
            {
                return Invalid(
                    httpContext,
                    "InvalidExpectedVersion",
                    "expectedVersion must be the base64 Version string from a previous read.");
            }

            try
            {
                var result = await updatePrice.UpdatePriceAsync(
                    normalized, request.UnitPrice, expectedVersion, cancellationToken);

                return result switch
                {
                    UpdatePriceResult.Updated updated => TypedResults.Ok(updated.Detail),
                    UpdatePriceResult.NotFound => NotFound(httpContext, normalized),
                    UpdatePriceResult.VersionConflict => TypedResults.Problem(ProblemDetailsWriter.Create(
                        httpContext,
                        StatusCodes.Status409Conflict,
                        "VersionConflict",
                        "The product was modified by someone else",
                        "expectedVersion no longer matches the stored row. Re-read the product and retry.")),
                    _ => Unavailable(httpContext)
                };
            }
            catch (DbException ex)
            {
                loggerFactory.CreateLogger("ProductEndpoints")
                    .LogError(ex, "SQL failure while updating the price of {Sku}", normalized);
                return Unavailable(httpContext);
            }
        });
    }

    private const string InvalidSkuDetail =
        "SKU must be 1-32 characters of A-Z, 0-9 or '-' after normalization.";

    private static bool TryDecodeVersion(string? encoded, out byte[] version)
    {
        version = [];
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[16];
        if (!Convert.TryFromBase64String(encoded, buffer, out var written) || written == 0)
        {
            return false;
        }

        version = buffer[..written].ToArray();
        return true;
    }

    private static IResult NotFound(HttpContext httpContext, string sku) =>
        TypedResults.Problem(ProblemDetailsWriter.Create(
            httpContext,
            StatusCodes.Status404NotFound,
            "ProductNotFound",
            "Product not found",
            $"No product exists with SKU '{sku}'."));

    private static IResult Invalid(HttpContext httpContext, string code, string detail) =>
        TypedResults.Problem(
            ProblemDetailsWriter.Create(httpContext, StatusCodes.Status400BadRequest, code, "Invalid request", detail));

    private static IResult Unavailable(HttpContext httpContext) =>
        TypedResults.Problem(
            ProblemDetailsWriter.Create(
                httpContext,
                StatusCodes.Status503ServiceUnavailable,
                "SqlUnavailable",
                "Data store is currently unavailable"));
}
