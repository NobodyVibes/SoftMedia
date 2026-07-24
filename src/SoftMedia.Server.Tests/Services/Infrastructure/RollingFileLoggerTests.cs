using Microsoft.Extensions.Logging;
using SoftMedia.Server.Services.Infrastructure;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Infrastructure;

/// SR-WI-064 — persistent rolling-file log sink: daily rotation, bounded retention,
/// an independent Warning+ floor, and the T6.6 token scrub (lines quoting media URLs
/// with ?token=/?access_token= JWTs must never persist the token value).
public sealed class RollingFileLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "softmedia-tests", "logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static string FileFor(string dir, DateTime utc)
        => Path.Combine(dir, $"softmedia-{utc:yyyyMMdd}.log");

    [Fact]
    public void BelowMinimumLevel_IsNotWritten_WarningAndAboveAre()
    {
        var now = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
        using (var provider = new RollingFileLoggerProvider(_dir, LogLevel.Warning, 7, () => now))
        {
            var logger = provider.CreateLogger("Test.Category");
            Assert.False(logger.IsEnabled(LogLevel.Information));
            Assert.True(logger.IsEnabled(LogLevel.Warning));

            logger.LogInformation("info-line-should-not-persist");
            logger.LogDebug("debug-line-should-not-persist");
            logger.LogWarning("warning-line-should-persist");
            logger.LogError(new InvalidOperationException("boom"), "error-line-should-persist");
        }

        var content = File.ReadAllText(FileFor(_dir, now));
        Assert.DoesNotContain("info-line-should-not-persist", content);
        Assert.DoesNotContain("debug-line-should-not-persist", content);
        Assert.Contains("warning-line-should-persist", content);
        Assert.Contains("error-line-should-persist", content);
        Assert.Contains("boom", content); // exception text is persisted too
        Assert.Contains("Test.Category", content);
    }

    [Fact]
    public void DayRollover_WritesToANewDailyFile()
    {
        var day1 = new DateTime(2026, 7, 24, 23, 59, 0, DateTimeKind.Utc);
        var now = day1;
        using (var provider = new RollingFileLoggerProvider(_dir, LogLevel.Warning, 7, () => now))
        {
            var logger = provider.CreateLogger("Rotate");
            logger.LogWarning("line-on-day-one");
            now = day1.AddMinutes(2); // crosses midnight into 2026-07-25
            logger.LogWarning("line-on-day-two");
        }

        Assert.Contains("line-on-day-one", File.ReadAllText(FileFor(_dir, day1)));
        var day2File = FileFor(_dir, day1.AddDays(1));
        Assert.True(File.Exists(day2File), "rollover must open a new daily file");
        var day2 = File.ReadAllText(day2File);
        Assert.Contains("line-on-day-two", day2);
        Assert.DoesNotContain("line-on-day-one", day2);
    }

    [Fact]
    public void Retention_DeletesFilesOlderThanTheWindow_KeepsThoseInside()
    {
        Directory.CreateDirectory(_dir);
        var today = new DateTime(2026, 7, 24, 8, 0, 0, DateTimeKind.Utc);
        var expired = FileFor(_dir, today.AddDays(-7));   // 8th file — out of a 7-day window
        var retained = FileFor(_dir, today.AddDays(-6));  // oldest still inside the window
        var unrelated = Path.Combine(_dir, "not-a-log.txt");
        File.WriteAllText(expired, "old");
        File.WriteAllText(retained, "keep");
        File.WriteAllText(unrelated, "keep");

        using (var provider = new RollingFileLoggerProvider(_dir, LogLevel.Warning, 7, () => today))
        {
            provider.CreateLogger("Retention").LogWarning("trigger-open");
        }

        Assert.False(File.Exists(expired), "file outside the retention window must be deleted");
        Assert.True(File.Exists(retained), "file inside the retention window must survive");
        Assert.True(File.Exists(unrelated), "non-sink files must never be touched");
        Assert.True(File.Exists(FileFor(_dir, today)));
    }

    [Fact]
    public void TokenValues_AreScrubbedFromMessageAndException()
    {
        // T6.6: Hosting.Diagnostics Warning lines can quote full media URLs whose
        // query string carries a JWT. Neither message nor exception text may
        // persist the value.
        var now = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
        using (var provider = new RollingFileLoggerProvider(_dir, LogLevel.Warning, 7, () => now))
        {
            var logger = provider.CreateLogger("Scrub");
            logger.LogWarning(
                "Request GET http://host/api/v1/stream/x/master.m3u8?token=eyJhbGciOi.SECRETJWT.sig&sub=2 slow");
            logger.LogError(
                new InvalidOperationException("failed url http://h/x.ts?access_token=SECRETVALUE&y=1"),
                "with exception");
        }

        var content = File.ReadAllText(FileFor(_dir, now));
        Assert.DoesNotContain("SECRETJWT", content);
        Assert.DoesNotContain("SECRETVALUE", content);
        Assert.Contains("token=[REDACTED]", content);
        Assert.Contains("access_token=[REDACTED]", content);
        // The rest of the line survives (the URL is still diagnosable).
        Assert.Contains("master.m3u8", content);
        Assert.Contains("&sub=2", content);
    }

    [Theory]
    [InlineData("no tokens here at all", "no tokens here at all")]
    [InlineData("?token=abc123", "?token=[REDACTED]")]
    [InlineData("&ACCESS_TOKEN=Zz.9-_x rest", "&ACCESS_TOKEN=[REDACTED] rest")]
    [InlineData("mediaToken renewal scheduled", "mediaToken renewal scheduled")]
    public void Scrub_RedactsOnlyTokenValues(string input, string expected)
    {
        Assert.Equal(expected, RollingFileLoggerProvider.Scrub(input));
    }

    [Fact]
    public void Dispose_Flushes_AndSubsequentLogsAreDropped()
    {
        var now = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
        var provider = new RollingFileLoggerProvider(_dir, LogLevel.Warning, 7, () => now);
        var logger = provider.CreateLogger("Disposal");
        logger.LogWarning("before-dispose");
        provider.Dispose();

        // Never throws, never resurrects the writer.
        logger.LogWarning("after-dispose");

        var content = File.ReadAllText(FileFor(_dir, now));
        Assert.Contains("before-dispose", content);
        Assert.DoesNotContain("after-dispose", content);
    }
}
