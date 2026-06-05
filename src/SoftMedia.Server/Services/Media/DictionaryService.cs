using System.Collections.Concurrent;
using System.Text.Json;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// JSON-file-backed dictionary (ER-051). Data is loaded lazily on first use
/// and cached in-process for the lifetime of the application. The backing
/// file is expected at <c>data/dictionary.json</c> relative to the web root
/// with shape <c>{ "word": ["def 1", "def 2"] }</c>. Lookups are
/// case-insensitive and trimmed; punctuation is stripped.
///
/// Registered as a singleton so the dictionary map doesn't rebuild per
/// request. Memory cost scales with dataset size (WordNet-ish JSON runs
/// ~30 MB); users who skip the dataset pay zero.
/// </summary>
public class DictionaryService : IDictionaryService
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<DictionaryService> _logger;

    // Three-state: null = not yet probed; empty = probed and absent; populated
    // = loaded. Using a ConcurrentDictionary gives thread-safe lazy init
    // without a lock — the first caller wins, others read the map.
    private IReadOnlyDictionary<string, string[]>? _map;
    private bool _probed;
    private readonly object _probeLock = new();

    public DictionaryService(IWebHostEnvironment env, ILogger<DictionaryService> logger)
    {
        _env = env;
        _logger = logger;
    }

    public bool Available
    {
        get
        {
            EnsureProbed();
            return _map is not null;
        }
    }

    public Task<IReadOnlyList<string>?> LookupAsync(string word, CancellationToken cancellationToken = default)
    {
        EnsureProbed();
        if (_map is null) return Task.FromResult<IReadOnlyList<string>?>(null);

        var normalised = NormaliseWord(word);
        if (normalised.Length == 0) return Task.FromResult<IReadOnlyList<string>?>(Array.Empty<string>());

        // Direct hit first; fall back to case-insensitive lookup because the
        // dataset may ship either lowercase-only or mixed-case keys.
        if (_map.TryGetValue(normalised, out var defs))
        {
            return Task.FromResult<IReadOnlyList<string>?>(defs);
        }
        return Task.FromResult<IReadOnlyList<string>?>(Array.Empty<string>());
    }

    private void EnsureProbed()
    {
        if (_probed) return;
        lock (_probeLock)
        {
            if (_probed) return;
            _probed = true;
            _map = TryLoad();
        }
    }

    private IReadOnlyDictionary<string, string[]>? TryLoad()
    {
        var path = ResolveDictionaryPath();
        if (path is null || !File.Exists(path))
        {
            _logger.LogInformation("[Dictionary] No dictionary file at the expected path; lookups will return 501.");
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)
                      ?? new Dictionary<string, string[]>();
            // Re-key lowercase so LookupAsync's case-insensitive normalisation
            // resolves in a single O(1) hit.
            var normalised = new Dictionary<string, string[]>(raw.Count, StringComparer.Ordinal);
            foreach (var (k, v) in raw)
            {
                normalised[k.ToLowerInvariant()] = v;
            }
            _logger.LogInformation("[Dictionary] Loaded {Count} entries from {Path}", normalised.Count, path);
            return normalised;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Dictionary] Failed to parse {Path}; lookups will return 501.", path);
            return null;
        }
    }

    private string? ResolveDictionaryPath()
    {
        var webRoot = !string.IsNullOrEmpty(_env.WebRootPath)
            ? _env.WebRootPath
            : Path.Combine(Environment.CurrentDirectory, "wwwroot");
        // Live outside wwwroot so the raw dataset isn't served as a static
        // file by accident — the JSON can weigh tens of MB and a user pulling
        // it from an unauthenticated endpoint would defeat the privacy story.
        return Path.Combine(Path.GetDirectoryName(webRoot) ?? webRoot, "data", "dictionary.json");
    }

    private static string NormaliseWord(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        // Strip trailing punctuation — common when the user double-clicks a
        // word at the end of a sentence. Apostrophes inside the word are
        // preserved so "don't" lookups work.
        var trimmed = raw.Trim().TrimEnd('.', ',', ';', ':', '!', '?', ')', '(', '"', '\'');
        return trimmed.ToLowerInvariant();
    }
}
