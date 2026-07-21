namespace SoftMedia.Server.Helpers;

/// <summary>
/// Picks which genre the home page's spotlight row features on a given day.
///
/// Pure and date-driven so the behaviour is directly testable — the properties that
/// matter here (a stable pick for a whole day, an even walk through the pool, no
/// discontinuity at the new year) are all statements about the calendar, and verifying
/// them through the full recommendation stack would be both slow and indirect.
///
/// Date-seeded rather than random on purpose. A random pick would reshuffle on every
/// refresh, differ between tabs and users, fight the client's 5-minute cache, and be
/// impossible to assert on. A day is the smallest unit that still feels curated.
/// </summary>
public static class GenreSpotlightRotation
{
    /// <summary>
    /// Select from <paramref name="orderedPool"/> for the day containing
    /// <paramref name="now"/>. The pool must already be filtered to genres with enough
    /// items to fill a row and ordered deterministically; the caller owns both.
    /// Returns null for an empty pool.
    /// </summary>
    public static string? Pick(IReadOnlyList<string> orderedPool, DateTimeOffset now)
    {
        if (orderedPool.Count == 0) return null;
        return orderedPool[IndexFor(orderedPool.Count, now)];
    }

    /// <summary>
    /// Index into a pool of <paramref name="poolSize"/> for the given moment.
    /// </summary>
    public static int IndexFor(int poolSize, DateTimeOffset now)
    {
        if (poolSize <= 0) throw new ArgumentOutOfRangeException(nameof(poolSize));

        // Days since 0001-01-01 — monotonic. DayOfYear would wrap 365 -> 1 and can
        // repeat (or skip) a genre across the new year whenever the pool size does not
        // divide the year evenly, which is the common case.
        var dayNumber = DateOnly.FromDateTime(now.UtcDateTime).DayNumber;
        return dayNumber % poolSize;
    }
}
