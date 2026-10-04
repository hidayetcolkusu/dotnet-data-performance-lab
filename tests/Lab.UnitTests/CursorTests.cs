using DataPerformanceLab.Catalog;
using Xunit;

namespace DataPerformanceLab.UnitTests;

public sealed class CursorTests
{
    [Fact]
    public void RoundTrip_PreservesValuesAndUtcKind()
    {
        var cursor = new ProductCursor(
            new DateTime(2026, 5, 1, 12, 30, 45, DateTimeKind.Utc).AddTicks(1234567),
            Id: 987654,
            CategoryId: 7);

        var encoded = cursor.Encode();

        Assert.True(ProductCursor.TryParse(encoded, 7, out var decoded, out var error), error);
        Assert.Equal(cursor, decoded);
        Assert.Equal(DateTimeKind.Utc, decoded!.CreatedAtUtc.Kind);
    }

    [Fact]
    public void Encode_IsDeterministic()
    {
        var cursor = new ProductCursor(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1, 1);

        Assert.Equal(cursor.Encode(), cursor.Encode());
    }

    [Fact]
    public void Decode_RejectsCorruptBase64()
    {
        Assert.False(ProductCursor.TryParse("not*valid*base64url!", 1, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Decode_RejectsCursorLongerThan512Characters()
    {
        var tooLong = new string('A', ProductCursor.MaxEncodedLength + 1);

        Assert.False(ProductCursor.TryParse(tooLong, 1, out _, out var error));
        Assert.Contains("512", error);
    }

    [Theory]
    [InlineData("bm90LXZlcnNpb24tMQ==")]
    [InlineData("")]
    [InlineData("   ")]
    public void Decode_RejectsMalformedPayload(string candidate)
    {
        Assert.False(ProductCursor.TryParse(candidate, 1, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Decode_RejectsUnknownVersion()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("99|638000000000000000|1|1");
        var encoded = System.Buffers.Text.Base64Url.EncodeToString(payload);

        Assert.False(ProductCursor.TryParse(encoded, 1, out _, out var error));
        Assert.Contains("malformed", error);
    }

    [Fact]
    public void Decode_CategoryMismatch_ReturnsExplicitError()
    {
        var cursor = new ProductCursor(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1, CategoryId: 1);
        var encoded = cursor.Encode();

        Assert.False(ProductCursor.TryParse(encoded, requestCategoryId: 2, out _, out var error));
        Assert.Contains("category", error);
    }
}
