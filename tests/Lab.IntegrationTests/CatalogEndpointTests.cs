using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DataPerformanceLab.Catalog;
using Xunit;

namespace DataPerformanceLab.IntegrationTests;

[Collection("sql")]
public sealed class CatalogEndpointTests(SqlServerFixture sql)
{
    private static readonly DateTime SameTimestamp = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Product NewProduct(string sku, int categoryId, bool isActive = true) => new()
    {
        Sku = sku,
        Name = $"Name {sku}",
        CategoryId = categoryId,
        UnitPrice = 9.99m,
        IsActive = isActive,
        Description = "synthetic description",
        CreatedAtUtc = SameTimestamp,
    };

    private async Task<(LabApiFactory Factory, string DatabaseName)> CreateApiWithFixedCatalogAsync()
    {
        var databaseName = await sql.CreateAndMigrateTestDatabaseAsync();

        await using (var db = sql.CreateContext(databaseName))
        {
            db.Categories.Add(new Category { Id = 1, Name = "Alpha" });
            db.Categories.Add(new Category { Id = 2, Name = "Beta" });

            for (var i = 1; i <= 25; i++)
            {
                db.Products.Add(NewProduct($"SKU-A{i:D3}", categoryId: 1));
            }

            for (var i = 1; i <= 5; i++)
            {
                db.Products.Add(NewProduct($"SKU-I{i:D3}", categoryId: 1, isActive: false));
            }

            for (var i = 1; i <= 10; i++)
            {
                db.Products.Add(NewProduct($"SKU-B{i:D3}", categoryId: 2));
            }

            await db.SaveChangesAsync();
        }

        return (new LabApiFactory(sql, databaseName), databaseName);
    }

    private static async Task<JsonDocument> GetListAsync(
        HttpClient client, int categoryId, int? pageSize = null, string? cursor = null)
    {
        var query = $"?categoryId={categoryId}";
        if (pageSize is not null)
        {
            query += $"&pageSize={pageSize}";
        }

        if (cursor is not null)
        {
            query += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        using var response = await client.GetAsync($"/api/products{query}");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EqualTimestamps_TwoPages_NoDuplicatesNoGaps()
    {
        var (factory, _) = await CreateApiWithFixedCatalogAsync();
        using var client = factory.CreateClient();

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;

        while (true)
        {
            using var page = await GetListAsync(client, categoryId: 1, pageSize: 20, cursor: cursor);
            var items = page.RootElement.GetProperty("items").EnumerateArray().ToList();
            var nextCursor = page.RootElement.GetProperty("nextCursor");

            Assert.True(items.Count > 0 && items.Count <= 20);
            seen.AddRange(items.Select(i => i.GetProperty("sku").GetString()!));
            pages++;

            if (nextCursor.ValueKind is JsonValueKind.Null)
            {
                break;
            }

            cursor = nextCursor.GetString();
        }

        var expected = Enumerable.Range(1, 25).Select(i => $"SKU-A{i:D3}").OrderBy(s => s).ToList();
        Assert.Equal(2, pages);
        Assert.Equal(25, seen.Count);
        Assert.Equal(expected, seen.OrderBy(s => s).ToList());
        Assert.DoesNotContain(seen, s => s.StartsWith("SKU-I"));
    }

    [Fact]
    public async Task SecondPage_ContinuesAfterLastReturnedRow()
    {
        var (factory, _) = await CreateApiWithFixedCatalogAsync();
        using var client = factory.CreateClient();

        using var first = await GetListAsync(client, categoryId: 1, pageSize: 10);
        var firstSkus = first.RootElement.GetProperty("items")
            .EnumerateArray().Select(i => i.GetProperty("sku").GetString()!).ToList();
        var nextCursor = first.RootElement.GetProperty("nextCursor").GetString();
        Assert.NotNull(nextCursor);

        using var second = await GetListAsync(client, categoryId: 1, pageSize: 10, cursor: nextCursor);
        var secondSkus = second.RootElement.GetProperty("items")
            .EnumerateArray().Select(i => i.GetProperty("sku").GetString()!).ToList();

        Assert.Equal(10, secondSkus.Count);
        Assert.Empty(firstSkus.Intersect(secondSkus));
    }

    [Fact]
    public async Task LastPage_WhenFewerThanPageSizeRemain_ReturnsNullCursor()
    {
        var (factory, _) = await CreateApiWithFixedCatalogAsync();
        using var client = factory.CreateClient();

        using var page = await GetListAsync(client, categoryId: 1, pageSize: 25);
        var count = page.RootElement.GetProperty("items").GetArrayLength();

        Assert.Equal(25, count);
        Assert.Equal(JsonValueKind.Null, page.RootElement.GetProperty("nextCursor").ValueKind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public async Task InvalidCategoryId_Returns400(int categoryId)
    {
        var (factory, _) = await CreateApiWithFixedCatalogAsync();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/products?categoryId={categoryId}&pageSize=50");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemJsonAsync(response, "InvalidCategory");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task InvalidPageSize_Returns400(int pageSize)
    {
        var (factory, _) = await CreateApiWithFixedCatalogAsync();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/products?categoryId=1&pageSize={pageSize}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemJsonAsync(response, "InvalidPageSize");
    }

    [Fact]
    public async Task CorruptCursor_Returns400()
    {
        var (factory, _) = await CreateApiWithFixedCatalogAsync();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/products?categoryId=1&pageSize=50&cursor=%24%24corrupt%24%24");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemJsonAsync(response, "InvalidCursor");
    }

    [Fact]
    public async Task CursorFromOtherCategory_Returns400()
    {
        var (factory, _) = await CreateApiWithFixedCatalogAsync();
        using var client = factory.CreateClient();

        using var first = await GetListAsync(client, categoryId: 2, pageSize: 5);
        var cursorFromCategory2 = first.RootElement.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursorFromCategory2);

        using var response = await client.GetAsync(
            $"/api/products?categoryId=1&pageSize=50&cursor={Uri.EscapeDataString(cursorFromCategory2!)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemJsonAsync(response, "InvalidCursor");
    }

    [Fact]
    public async Task Detail_BySku_IsCaseInsensitive_AndReturnsFullDto()
    {
        var (factory, _) = await CreateApiWithFixedCatalogAsync();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/products/sku-a001");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("SKU-A001", detail.GetProperty("sku").GetString());
        Assert.Equal(1, detail.GetProperty("categoryId").GetInt32());
        Assert.Equal(9.99m, detail.GetProperty("unitPrice").GetDecimal());
        Assert.True(detail.GetProperty("isActive").GetBoolean());
        Assert.Equal("synthetic description", detail.GetProperty("description").GetString());

        var version = detail.GetProperty("version").GetString()!;
        Assert.Equal(8, Convert.FromBase64String(version).Length);
    }

    [Fact]
    public async Task Detail_UnknownSku_Returns404()
    {
        var (factory, _) = await CreateApiWithFixedCatalogAsync();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/products/SKU-DOES-NOT-EXIST");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ErrorResponses_UseProblemJson_EvenWithTextPlainAccept()
    {
        var (factory, _) = await CreateApiWithFixedCatalogAsync();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/products?categoryId=99");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(response.Content.Headers.ContentType);
        Assert.Contains("json", response.Content.Headers.ContentType.MediaType);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("InvalidCategory", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
        Assert.False(body.TryGetProperty("stack", out _));
        Assert.False(body.TryGetProperty("exception", out _));
    }

    [Fact]
    public async Task SqlUnavailable_Returns503()
    {
        using var factory = new UnreachableSqlApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/products?categoryId=1&pageSize=50");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("SqlUnavailable", body.GetProperty("code").GetString());
    }

    private static async Task AssertProblemJsonAsync(HttpResponseMessage response, string expectedCode)
    {
        Assert.NotNull(response.Content.Headers.ContentType);
        Assert.Contains("json", response.Content.Headers.ContentType.MediaType);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(expectedCode, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
    }
}
