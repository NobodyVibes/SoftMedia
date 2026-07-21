using SoftMedia.Server.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Helpers;

/// Daily rotation of the home page's genre spotlight. The properties worth pinning are
/// all statements about the calendar: a pick that holds for a whole day, an even walk
/// through the pool, and no discontinuity at the new year.
public class GenreSpotlightRotationTests
{
    private static readonly string[] Pool =
        { "Adventure", "Comedy", "Drama", "Action", "Science-Fiction" };

    private static DateTimeOffset Utc(int y, int m, int d, int hour = 12) =>
        new(new DateTime(y, m, d, hour, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void PickIsStableForEveryHourOfTheSameDay()
    {
        // The whole point of date-seeding: refreshing the page must not reshuffle it.
        var picks = Enumerable.Range(0, 24)
            .Select(h => GenreSpotlightRotation.Pick(Pool, Utc(2026, 7, 19, h)))
            .Distinct()
            .ToList();

        Assert.Single(picks);
    }

    [Fact]
    public void ConsecutiveDaysPickDifferentGenres()
    {
        var monday = GenreSpotlightRotation.Pick(Pool, Utc(2026, 7, 19));
        var tuesday = GenreSpotlightRotation.Pick(Pool, Utc(2026, 7, 20));

        Assert.NotEqual(monday, tuesday);
    }

    [Fact]
    public void WalksTheWholePoolBeforeRepeating()
    {
        var start = Utc(2026, 7, 19);
        var picks = Enumerable.Range(0, Pool.Length)
            .Select(i => GenreSpotlightRotation.Pick(Pool, start.AddDays(i))!)
            .ToList();

        // Every genre gets its turn — no genre is starved and none repeats early.
        Assert.Equal(Pool.Length, picks.Distinct().Count());
        Assert.Equal(Pool.OrderBy(x => x), picks.OrderBy(x => x));
    }

    [Fact]
    public void RepeatsExactlyOnePoolLengthLater()
    {
        var start = Utc(2026, 7, 19);

        Assert.Equal(
            GenreSpotlightRotation.Pick(Pool, start),
            GenreSpotlightRotation.Pick(Pool, start.AddDays(Pool.Length)));
    }

    /// <summary>
    /// The reason the implementation uses days-since-epoch rather than DayOfYear:
    /// DayOfYear wraps 365 -> 1, so 31 Dec and 1 Jan land on the SAME index whenever
    /// the pool size divides 364 — a visible stutter once a year that nobody would
    /// catch by hand.
    ///
    /// Pool size matters enormously here. This originally tested only the 5-genre pool,
    /// where 365 % 5 = 0 and 1 % 5 = 1 differ by luck, so it passed against a
    /// deliberately broken DayOfYear implementation. Sweeping the sizes is what makes
    /// this a real guard: 2, 4 and 7 all divide 364 and expose the bug.
    /// </summary>
    [Theory]
    [InlineData(2026)] // -> 2027, both common years
    [InlineData(2027)] // -> 2028, into a leap year
    [InlineData(2028)] // -> 2029, out of a leap year
    public void DoesNotStutterAcrossTheNewYearForAnyPoolSize(int year)
    {
        var newYearsEve = Utc(year, 12, 31);
        var newYearsDay = Utc(year + 1, 1, 1);

        for (var poolSize = 2; poolSize <= 12; poolSize++)
        {
            Assert.NotEqual(
                GenreSpotlightRotation.IndexFor(poolSize, newYearsEve),
                GenreSpotlightRotation.IndexFor(poolSize, newYearsDay));
        }
    }

    /// <summary>
    /// Generalises the above: NO two consecutive days may ever pick the same index, for
    /// any pool size, anywhere in a multi-year sweep. Any calendar arithmetic that
    /// stutters — at a year end, a leap day, or a month boundary — fails here.
    /// </summary>
    [Fact]
    public void NoTwoConsecutiveDaysEverPickTheSameIndex()
    {
        var start = Utc(2026, 1, 1);
        for (var poolSize = 2; poolSize <= 12; poolSize++)
        {
            for (var day = 0; day < 1500; day++) // ~4 years, spanning a leap year
            {
                var today = GenreSpotlightRotation.IndexFor(poolSize, start.AddDays(day));
                var tomorrow = GenreSpotlightRotation.IndexFor(poolSize, start.AddDays(day + 1));
                Assert.True(today != tomorrow,
                    $"pool={poolSize} stuttered on {start.AddDays(day):yyyy-MM-dd} -> index {today}");
            }
        }
    }

    [Fact]
    public void DoesNotStutterAcrossALeapYearBoundary()
    {
        // 2028 is a leap year; 2027 -> 2028 crosses a 365-day year, 2028 -> 2029 a 366.
        foreach (var (eve, day) in new[]
        {
            (Utc(2027, 12, 31), Utc(2028, 1, 1)),
            (Utc(2028, 12, 31), Utc(2029, 1, 1)),
            (Utc(2028, 2, 28), Utc(2028, 2, 29)),   // leap day itself
            (Utc(2028, 2, 29), Utc(2028, 3, 1)),
        })
        {
            Assert.NotEqual(
                GenreSpotlightRotation.Pick(Pool, eve),
                GenreSpotlightRotation.Pick(Pool, day));
        }
    }

    [Fact]
    public void UsesUtcSoTheDayBoundaryIsTheSameForEveryViewer()
    {
        // Same instant, different offsets — everyone in the house sees one shelf.
        var instant = new DateTimeOffset(2026, 7, 19, 23, 30, 0, TimeSpan.Zero);
        var sameInstantInTokyo = instant.ToOffset(TimeSpan.FromHours(9));

        Assert.Equal(
            GenreSpotlightRotation.Pick(Pool, instant),
            GenreSpotlightRotation.Pick(Pool, sameInstantInTokyo));
    }

    [Fact]
    public void ReturnsNullForAnEmptyPoolRatherThanThrowing()
    {
        // No genre has enough items — the row self-suppresses, it does not crash the
        // whole home-rows response.
        Assert.Null(GenreSpotlightRotation.Pick(Array.Empty<string>(), Utc(2026, 7, 19)));
    }

    [Fact]
    public void ASinglePoolEntryIsAlwaysPicked()
    {
        var single = new[] { "Adventure" };
        foreach (var offset in new[] { 0, 1, 7, 365 })
            Assert.Equal("Adventure", GenreSpotlightRotation.Pick(single, Utc(2026, 7, 19).AddDays(offset)));
    }

    [Fact]
    public void IndexIsAlwaysWithinBounds()
    {
        // Guards against a negative modulo, which would throw on indexing.
        var start = Utc(2026, 1, 1);
        for (var poolSize = 1; poolSize <= 12; poolSize++)
        {
            for (var day = 0; day < 400; day++)
            {
                var index = GenreSpotlightRotation.IndexFor(poolSize, start.AddDays(day));
                Assert.InRange(index, 0, poolSize - 1);
            }
        }
    }

    [Fact]
    public void RejectsANonPositivePoolSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GenreSpotlightRotation.IndexFor(0, Utc(2026, 7, 19)));
    }
}
