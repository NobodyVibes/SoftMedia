using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoftMedia.Server.Helpers;

/// <summary>
/// SR-WI-060 — every DateTime the API emits carries an explicit UTC marker.
///
/// Storage is consistently UTC, but SQLite round-trips <see cref="DateTime"/> with
/// <see cref="DateTimeKind.Unspecified"/>, so entity-sourced timestamps serialized
/// WITHOUT a "Z" suffix while fresh <c>DateTime.UtcNow</c> values got one. JavaScript's
/// <c>new Date("2026-07-24T10:00:00")</c> parses the unsuffixed form as LOCAL time,
/// silently shifting every persisted timestamp by the client's UTC offset.
///
/// Write: Unspecified is stamped as UTC (that is what it is), Local is converted, and
/// the value is emitted in round-trip ISO-8601 with the "Z" suffix.
/// Read: tolerant — accepts "Z", explicit offsets, and bare ISO strings (assumed UTC,
/// mirroring the write-side contract); always yields <see cref="DateTimeKind.Utc"/>.
///
/// <see cref="Nullable{DateTime}"/> is handled by System.Text.Json's built-in nullable
/// wrapper around this converter (nulls never reach Read/Write).
/// </summary>
public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new JsonException("Expected a non-empty ISO-8601 date-time string.");
        }

        // DateTimeOffset.Parse handles both offset-carrying and bare forms;
        // AssumeUniversal makes the bare form UTC instead of machine-local.
        if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dto))
        {
            return dto.UtcDateTime;
        }

        throw new JsonException($"Invalid ISO-8601 date-time: '{raw}'.");
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            // SQLite round-trip: stored UTC comes back Unspecified — stamp, don't shift.
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        writer.WriteStringValue(utc.ToString("O", CultureInfo.InvariantCulture));
    }
}
