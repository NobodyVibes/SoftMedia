using System.Text.Json;
using SoftMedia.Server.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Helpers;

/// SR-WI-060 — DateTime JSON contract: every serialized value carries an explicit UTC
/// marker (SQLite round-trips stored-UTC values as Kind=Unspecified, which used to
/// serialize without a "Z" and be parsed as LOCAL time by JS clients), and reads are
/// tolerant of Z / offset / bare forms, always yielding Kind=Utc.
public class UtcDateTimeJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new UtcDateTimeJsonConverter());
        return options;
    }

    private sealed record Payload(DateTime Stamp, DateTime? MaybeStamp);

    [Fact]
    public void UnspecifiedKind_SerializesWithZSuffix_WithoutShifting()
    {
        var unspecified = new DateTime(2026, 7, 24, 10, 30, 0, DateTimeKind.Unspecified);
        var json = JsonSerializer.Serialize(unspecified, Options);
        Assert.Equal("\"2026-07-24T10:30:00.0000000Z\"", json);
    }

    [Fact]
    public void UtcKind_SerializesWithZSuffix()
    {
        var utc = new DateTime(2026, 7, 24, 10, 30, 0, DateTimeKind.Utc);
        Assert.Equal("\"2026-07-24T10:30:00.0000000Z\"", JsonSerializer.Serialize(utc, Options));
    }

    [Fact]
    public void LocalKind_IsConvertedToUtc_NotStamped()
    {
        var local = new DateTime(2026, 7, 24, 10, 30, 0, DateTimeKind.Local);
        var json = JsonSerializer.Serialize(local, Options);
        var expected = local.ToUniversalTime().ToString("O");
        Assert.Equal($"\"{expected}\"", json);
        Assert.EndsWith("Z\"", json);
    }

    [Fact]
    public void NullableDateTime_NullAndValue_BothWork()
    {
        // The framework's nullable wrapper must route DateTime? through this converter.
        var payload = new Payload(
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified),
            null);
        var json = JsonSerializer.Serialize(payload, Options);
        Assert.Contains("2026-01-02T03:04:05.0000000Z", json);
        Assert.Contains("\"MaybeStamp\":null", json);

        var withValue = payload with { MaybeStamp = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Unspecified) };
        Assert.Contains("2026-01-03T00:00:00.0000000Z", JsonSerializer.Serialize(withValue, Options));
    }

    [Theory]
    [InlineData("\"2026-07-24T10:30:00Z\"", 10, 30)]           // explicit UTC
    [InlineData("\"2026-07-24T12:30:00+02:00\"", 10, 30)]      // offset — adjusted to UTC
    [InlineData("\"2026-07-24T10:30:00\"", 10, 30)]            // bare — assumed UTC
    [InlineData("\"2026-07-24T10:30:00.1234567Z\"", 10, 30)]   // fractional seconds
    public void Read_IsTolerant_AndAlwaysYieldsUtcKind(string json, int hour, int minute)
    {
        var value = JsonSerializer.Deserialize<DateTime>(json, Options);
        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(new DateTime(2026, 7, 24), value.Date);
        Assert.Equal(hour, value.Hour);
        Assert.Equal(minute, value.Minute);
    }

    [Theory]
    [InlineData("\"not-a-date\"")]
    [InlineData("\"\"")]
    public void Read_InvalidInput_ThrowsJsonException(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DateTime>(json, Options));
    }

    [Fact]
    public void RoundTrip_PreservesTheInstant()
    {
        var original = new DateTime(2026, 7, 24, 18, 3, 12, 456, DateTimeKind.Utc).AddTicks(7890);
        var json = JsonSerializer.Serialize(original, Options);
        var back = JsonSerializer.Deserialize<DateTime>(json, Options);
        Assert.Equal(original, back);
        Assert.Equal(DateTimeKind.Utc, back.Kind);
    }
}
