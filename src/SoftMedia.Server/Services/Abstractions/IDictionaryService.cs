namespace SoftMedia.Server.Services.Abstractions;

/// <summary>
/// ER-051: offline dictionary lookups. Backed by a JSON file the user (or
/// installer) drops at a conventional path; when the file is absent the
/// service reports <see cref="Available"/> false and <see cref="LookupAsync"/>
/// returns null. The controller translates "not available" into a 501 so the
/// client can render a clear "install the dictionary" state.
///
/// Data shape (normalised on load): a map of word → array of definitions.
/// Simple "word" → [definition, definition] JSON is the MVP format; a later
/// task can migrate to a WordNet bundler without changing the API.
/// </summary>
public interface IDictionaryService
{
    /// <summary>True when a dictionary file is present and successfully loaded.</summary>
    bool Available { get; }

    /// <summary>
    /// Returns a list of definitions for the word, or null when the dictionary
    /// is unavailable. An empty list means the word is not in the dataset.
    /// </summary>
    Task<IReadOnlyList<string>?> LookupAsync(string word, CancellationToken cancellationToken = default);
}
