namespace SoftMedia.Server.Helpers;

/// <summary>
/// The decision half of a genre normalisation pass, as a pure function over plain
/// values: which genre row survives under which canonical name, which rows retire,
/// and what the item→genre links become.
///
/// Kept free of EF and entity types on purpose. Deciding a merge is fiddly — a row
/// that expands to two canonical names can survive as neither, renaming before
/// deleting collides with the UNIQUE index, and a bad plan silently strips an item's
/// whole taxonomy. That logic deserves direct tests against real data, which it can
/// only have if it does not need a database to run.
/// <see cref="Services.Media.GenreMaintenanceService"/> owns the writing.
/// </summary>
public sealed class GenreNormalizationPlan
{
    /// <summary>Canonical name → id of the row that should carry it; null = create a new row.</summary>
    public required IReadOnlyDictionary<string, int?> TargetIdByName { get; init; }

    /// <summary>Genre rows that must be deleted (superseded, split, or pure junk).</summary>
    public required IReadOnlyList<int> RetiredGenreIds { get; init; }

    /// <summary>Item → the canonical genre names it should end up with.</summary>
    public required IReadOnlyDictionary<Guid, HashSet<string>> DesiredLinksByItem { get; init; }

    public required int GenresBefore { get; init; }
    public required int GenresAfter { get; init; }
    public required int GenresDropped { get; init; }
    public required int GenresCreated { get; init; }
    public required int LinksBefore { get; init; }
    public required int LinksAfter { get; init; }

    /// <summary>
    /// Items that hold at least one genre today but would hold none afterwards.
    /// Must be zero before a plan is applied — see the guard in the service.
    /// </summary>
    public required int ItemsLeftWithNoGenres { get; init; }

    /// <summary>True when applying this plan would change nothing.</summary>
    public bool IsNoOp => RetiredGenreIds.Count == 0 && GenresCreated == 0
                          && LinksBefore == LinksAfter && GenresBefore == GenresAfter;

    public static GenreNormalizationPlan Build(
        IReadOnlyList<(int Id, string Name)> genres,
        IReadOnlyList<(Guid ItemId, int GenreId)> links)
    {
        // Every row → the canonical name(s) it becomes. Empty means pure junk.
        var expansion = genres.ToDictionary(g => g.Id, g => GenreNormalizer.Normalize(g.Name).ToList());

        var canonicalNames = expansion.Values
            .SelectMany(v => v)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Only a row mapping to EXACTLY ONE canonical name may survive: a row like
        // "FICTION / Horror" becomes two genres and so can be neither of them.
        var targetIdByName = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        var survivors = new HashSet<int>();
        var created = 0;

        foreach (var name in canonicalNames)
        {
            var winner = genres
                .Where(g => expansion[g.Id].Count == 1
                            && string.Equals(expansion[g.Id][0], name, StringComparison.OrdinalIgnoreCase))
                // Prefer a row already spelled canonically, so the common case renames nothing.
                .OrderByDescending(g => string.Equals(g.Name, name, StringComparison.Ordinal))
                .ThenBy(g => g.Id)
                .Select(g => (int?)g.Id)
                .FirstOrDefault();

            targetIdByName[name] = winner;
            if (winner.HasValue) survivors.Add(winner.Value);
            else created++;
        }

        var retired = genres.Where(g => !survivors.Contains(g.Id)).Select(g => g.Id).ToList();

        // Rebuild links: (item, oldGenre) becomes (item, canonical) for every name the
        // old row expands to, de-duplicated per item.
        var desired = new Dictionary<Guid, HashSet<string>>();
        foreach (var (itemId, genreId) in links)
        {
            if (!expansion.TryGetValue(genreId, out var names)) continue;
            if (!desired.TryGetValue(itemId, out var set))
                desired[itemId] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names) set.Add(n);
        }

        var itemsWithGenresBefore = links.Select(l => l.ItemId).Distinct().Count();
        var itemsWithGenresAfter = desired.Count(kv => kv.Value.Count > 0);

        return new GenreNormalizationPlan
        {
            TargetIdByName = targetIdByName,
            RetiredGenreIds = retired,
            DesiredLinksByItem = desired,
            GenresBefore = genres.Count,
            GenresAfter = canonicalNames.Count,
            GenresDropped = expansion.Count(kv => kv.Value.Count == 0),
            GenresCreated = created,
            LinksBefore = links.Count,
            LinksAfter = desired.Sum(kv => kv.Value.Count),
            ItemsLeftWithNoGenres = itemsWithGenresBefore - itemsWithGenresAfter,
        };
    }
}
