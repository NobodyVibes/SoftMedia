using SoftMedia.Server.Services.Infrastructure;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Infrastructure;

/// NR-WI-011 — the runtime log-level switch and the log ring buffer.
public class RuntimeLogLevelTests
{
    [Fact]
    public void Apply_SetsDefaultKey_AndNormalizesCasing()
    {
        var provider = new RuntimeLogLevelProvider();

        provider.Apply("debug");

        Assert.Equal("Debug", provider.Current);
        Assert.True(provider.TryGet("Logging:LogLevel:Default", out var value));
        Assert.Equal("Debug", value);
    }

    [Fact]
    public void Apply_InvalidLevel_IsIgnored()
    {
        var provider = new RuntimeLogLevelProvider();
        provider.Apply("Verbose"); // not a Microsoft.Extensions.Logging level
        Assert.Equal("Information", provider.Current);
        Assert.False(provider.TryGet("Logging:LogLevel:Default", out _));
    }

    [Fact]
    public void Apply_OnlyTouchesTheDefaultKey_CategoryPinsUntouched()
    {
        // T6.6 interaction: the provider must never contribute category keys — the
        // Hosting.Diagnostics=Warning pin in appsettings has to keep outranking it.
        var provider = new RuntimeLogLevelProvider();
        provider.Apply("Trace");
        Assert.False(provider.TryGet("Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics", out _));
    }

    [Fact]
    public void RingBuffer_CapsEntries_AndFiltersByLevel()
    {
        var buffer = new LogRingBuffer();
        for (var i = 0; i < 2500; i++)
        {
            buffer.Add(new LogEntry(DateTime.UtcNow, i % 2 == 0 ? "Information" : "Warning", "Test", $"msg {i}", null));
        }

        var all = buffer.Snapshot(take: 2000, minLevel: null);
        Assert.True(all.Count <= 2000);

        var warnings = buffer.Snapshot(take: 2000, minLevel: "Warning");
        Assert.All(warnings, e => Assert.NotEqual("Information", e.Level));
        // The oldest 500 were evicted; the newest entry is retained.
        Assert.Contains(all, e => e.Message == "msg 2499");
    }
}
