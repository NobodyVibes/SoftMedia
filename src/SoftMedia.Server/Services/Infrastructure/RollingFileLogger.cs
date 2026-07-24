using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SoftMedia.Server.Services.Infrastructure;

/// <summary>
/// SR-WI-064 — DI-visible pointer to the persistent log sink's directory so the
/// Server &amp; Network admin page can tell the operator where the files live.
/// </summary>
public sealed record FileLogSinkInfo(string Directory);

/// <summary>
/// SR-WI-064 — persistent log sink. Until now SoftMedia logged to the console and a
/// 2000-entry in-memory ring only, so after a crash there was nothing on disk to
/// diagnose with. This provider writes daily rolling files (softmedia-yyyyMMdd.log)
/// under the content root's data/logs directory with a bounded retention window.
///
/// Design points:
/// - Its minimum level (default Warning) is INDEPENDENT of the console/ring levels:
///   the operator raising the viewer to Debug must not start churning the disk.
/// - Thread-safe via a single lock around the writer; Warning+ volume is low enough
///   that write-through with per-line flush is the simplest correct choice, and it
///   means a hard crash loses at most the line being written.
/// - Token scrubbing (see <see cref="Scrub"/>): the T6.6 pin
///   (Microsoft.AspNetCore.Hosting.Diagnostics = Warning in appsettings) exists because
///   that category's request lines log full media URLs carrying ?token=/?access_token=
///   JWTs. This sink's own floor is Warning+, which still RECORDS such lines when they
///   fire at Warning — so every rendered line (message AND exception text) is scrubbed
///   before it touches the disk. Never remove the pin, and never bypass the scrub.
/// - Logging must never take the app down: all IO failures are swallowed.
/// </summary>
public sealed class RollingFileLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
{
    private readonly string _directory;
    private readonly Microsoft.Extensions.Logging.LogLevel _minLevel;
    private readonly int _retentionDays;
    private readonly Func<DateTime> _utcNow;

    private readonly object _sync = new();
    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private bool _disposed;

    private const string FilePrefix = "softmedia-";
    private const string FileExtension = ".log";

    /// <summary>
    /// T6.6 guard: strip token-bearing query values from rendered log text.
    /// Matches ?token= / &amp;access_token= (any casing) and redacts the value up to the
    /// next delimiter, so a Warning-level line quoting a media URL cannot persist a JWT.
    /// </summary>
    private static readonly Regex TokenScrubber = new(
        @"(?<key>(?:access_)?token)\s*=\s*[^&\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public RollingFileLoggerProvider(
        string directory,
        Microsoft.Extensions.Logging.LogLevel minLevel = Microsoft.Extensions.Logging.LogLevel.Warning,
        int retentionDays = 7,
        Func<DateTime>? utcNow = null)
    {
        _directory = directory;
        _minLevel = minLevel;
        _retentionDays = Math.Max(1, retentionDays);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
        => new RollingFileLogger(this, categoryName);

    /// <summary>Public so tests can drive it directly (project convention; no InternalsVisibleTo).</summary>
    public static string Scrub(string text)
        => text.Contains("token", StringComparison.OrdinalIgnoreCase)
            ? TokenScrubber.Replace(text, "${key}=[REDACTED]")
            : text;

    private void Write(Microsoft.Extensions.Logging.LogLevel level, string category, string message, Exception? exception)
    {
        var now = _utcNow();
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append("Z [").Append(level).Append("] ")
            .Append(category).Append(" — ")
            .Append(Scrub(message));
        if (exception is not null)
        {
            line.AppendLine().Append(Scrub(exception.ToString()));
        }

        lock (_sync)
        {
            if (_disposed) return;
            try
            {
                EnsureWriterFor(DateOnly.FromDateTime(now));
                _writer!.WriteLine(line.ToString());
                _writer.Flush();
            }
            catch
            {
                // A log sink must never throw into the app. Drop the line; the console
                // and ring buffer still have it.
            }
        }
    }

    /// <summary>Opens (or rolls) the day's file and prunes expired ones. Caller holds the lock.</summary>
    private void EnsureWriterFor(DateOnly date)
    {
        if (_writer is not null && _writerDate == date) return;

        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;

        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"{FilePrefix}{date:yyyyMMdd}{FileExtension}");
        // FileShare.ReadWrite: a second instance (tests, an operator tailing the file)
        // must never wedge the sink.
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _writerDate = date;

        PruneExpired(date);
    }

    /// <summary>Deletes softmedia-*.log files whose filename date fell out of the retention window.</summary>
    private void PruneExpired(DateOnly today)
    {
        try
        {
            var oldest = today.AddDays(-(_retentionDays - 1));
            foreach (var file in Directory.EnumerateFiles(_directory, $"{FilePrefix}*{FileExtension}"))
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                var datePart = stem.Substring(FilePrefix.Length);
                if (DateOnly.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var fileDate)
                    && fileDate < oldest)
                {
                    try { File.Delete(file); }
                    catch { /* in use elsewhere — retried on the next roll */ }
                }
            }
        }
        catch
        {
            // Retention is best-effort; never let it break logging.
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch { /* flush-on-dispose is best-effort */ }
            _writer = null;
        }
    }

    private sealed class RollingFileLogger : Microsoft.Extensions.Logging.ILogger
    {
        private readonly RollingFileLoggerProvider _provider;
        private readonly string _category;

        public RollingFileLogger(RollingFileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Independent floor — deliberately NOT tied to the global/runtime level the
        // console and ring buffer follow (see class doc).
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel)
            => logLevel != Microsoft.Extensions.Logging.LogLevel.None && logLevel >= _provider._minLevel;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _provider.Write(logLevel, _category, formatter(state, exception), exception);
        }
    }
}
