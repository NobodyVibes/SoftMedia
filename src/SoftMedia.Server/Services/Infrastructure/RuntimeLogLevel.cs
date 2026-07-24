using Microsoft.Extensions.Primitives;

namespace SoftMedia.Server.Services.Infrastructure;

/// <summary>
/// NR-WI-011 — runtime-adjustable log level without a restart. Registered as BOTH a
/// configuration source (so the logging system's IOptionsMonitor reload picks changes
/// up) and a DI singleton (so SettingsController can apply the persisted setting).
///
/// It contributes ONLY the <c>Logging:LogLevel:Default</c> key. Category pins in
/// appsettings — critically <c>Microsoft.AspNetCore.Hosting.Diagnostics=Warning</c>
/// (T6.6: request-URL lines carry ?token= JWTs) — are more specific and always win,
/// so raising Default to Debug here can never reopen that leak.
/// </summary>
public interface IRuntimeLogLevel
{
    string Current { get; }
    void Apply(string level);
}

public class RuntimeLogLevelProvider : ConfigurationProvider, IConfigurationSource, IRuntimeLogLevel
{
    private static readonly string[] ValidLevels =
        { "Trace", "Debug", "Information", "Warning", "Error", "Critical", "None" };

    public string Current { get; private set; } = "Information";

    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

    public void Apply(string level)
    {
        var normalized = ValidLevels.FirstOrDefault(l => l.Equals(level, StringComparison.OrdinalIgnoreCase));
        if (normalized is null || normalized == Current) return;

        Current = normalized;
        Data["Logging:LogLevel:Default"] = normalized;
        OnReload(); // LoggerFactory listens to configuration reload tokens
    }
}

/// <summary>
/// NR-WI-011 — bounded in-memory log capture for the admin log viewer. SoftMedia logs
/// to the console by default (no file sink), so "show me the logs" needs a source the
/// server itself can serve. A capped ring buffer holds the most recent entries;
/// nothing is persisted and nothing leaves the machine.
/// </summary>
public record LogEntry(DateTime TimestampUtc, string Level, string Category, string Message, string? Exception);

public class LogRingBuffer
{
    private const int Capacity = 2000;
    private readonly object _lock = new();
    private readonly Queue<LogEntry> _entries = new(Capacity);

    public void Add(LogEntry entry)
    {
        lock (_lock)
        {
            if (_entries.Count >= Capacity) _entries.Dequeue();
            _entries.Enqueue(entry);
        }
    }

    public IReadOnlyList<LogEntry> Snapshot(int take, string? minLevel)
    {
        var threshold = ParseLevel(minLevel);
        lock (_lock)
        {
            return _entries
                .Where(e => ParseLevel(e.Level) >= threshold)
                .TakeLast(Math.Clamp(take, 1, Capacity))
                .ToList();
        }
    }

    private static int ParseLevel(string? level) => level?.ToLowerInvariant() switch
    {
        "trace" => 0,
        "debug" => 1,
        "information" or "info" => 2,
        "warning" or "warn" => 3,
        "error" => 4,
        "critical" => 5,
        _ => 2, // default floor: Information
    };
}

public sealed class RingBufferLoggerProvider : ILoggerProvider
{
    private readonly LogRingBuffer _buffer;

    public RingBufferLoggerProvider(LogRingBuffer buffer) => _buffer = buffer;

    public ILogger CreateLogger(string categoryName) => new RingBufferLogger(_buffer, categoryName);

    public void Dispose() { }

    private sealed class RingBufferLogger : ILogger
    {
        private readonly LogRingBuffer _buffer;
        private readonly string _category;

        public RingBufferLogger(LogRingBuffer buffer, string category)
        {
            _buffer = buffer;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // No independent floor: the buffer captures whatever the GLOBAL filter passes,
        // so selecting Debug in settings makes Debug visible in the in-app viewer —
        // the control's effect is visible where the operator is looking. At the
        // Information default this is identical to a hard floor; under Debug the
        // churn is the operator's explicit, temporary choice (and the category pins
        // keep framework noise out regardless).
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _buffer.Add(new LogEntry(
                DateTime.UtcNow,
                logLevel.ToString(),
                _category,
                formatter(state, exception),
                exception?.ToString()));
        }
    }
}
