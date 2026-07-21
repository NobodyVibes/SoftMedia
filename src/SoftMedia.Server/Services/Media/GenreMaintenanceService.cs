using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Media;

/// <summary>Outcome of a genre normalisation pass.</summary>
public record GenreNormalizationResult(
    int GenresBefore,
    int GenresAfter,
    int GenresCreatedFromSplits,
    int GenresDropped,
    int GenresRetired,
    int LinksBefore,
    int LinksAfter,
    int ItemsLeftWithNoGenres,
    bool DryRun,
    List<string> Examples);

public interface IGenreMaintenanceService
{
    /// <summary>
    /// Collapse the Genre table onto <see cref="GenreNormalizer"/>'s canonical form:
    /// merge case-variants, split BISAC subject paths, drop non-genre headings, and
    /// re-point every MediaItemGenre link at the surviving row.
    /// Pass <paramref name="dryRun"/> to compute the outcome without writing.
    /// </summary>
    Task<GenreNormalizationResult> NormalizeAsync(bool dryRun, CancellationToken ct = default);
}

/// <summary>
/// Applies a <see cref="GenreNormalizationPlan"/> to the database. All the deciding
/// lives in the plan; this owns only the write order and the safety guard.
///
/// Admin-triggered rather than a startup migration: it rewrites user-visible taxonomy
/// and deletes rows, which should be a deliberate act with a dry run available first,
/// not a silent side effect of deploying. Idempotent — a second run over clean data
/// reports a no-op and writes nothing.
/// </summary>
public class GenreMaintenanceService : IGenreMaintenanceService
{
    private readonly AppDbContext _db;
    private readonly ILogger<GenreMaintenanceService> _logger;

    public GenreMaintenanceService(AppDbContext db, ILogger<GenreMaintenanceService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<GenreNormalizationResult> NormalizeAsync(bool dryRun, CancellationToken ct = default)
    {
        var genreEntities = await _db.Genres.ToListAsync(ct);
        var linkEntities = await _db.MediaItemGenres.ToListAsync(ct);

        var plan = GenreNormalizationPlan.Build(
            genreEntities.Select(g => (g.Id, g.Name)).ToList(),
            linkEntities.Select(l => (l.MediaItemId, l.GenreId)).ToList());

        var result = new GenreNormalizationResult(
            GenresBefore: plan.GenresBefore,
            GenresAfter: plan.GenresAfter,
            GenresCreatedFromSplits: plan.GenresCreated,
            GenresDropped: plan.GenresDropped,
            GenresRetired: plan.RetiredGenreIds.Count,
            LinksBefore: plan.LinksBefore,
            LinksAfter: plan.LinksAfter,
            ItemsLeftWithNoGenres: plan.ItemsLeftWithNoGenres,
            DryRun: dryRun,
            Examples: BuildExamples(genreEntities));

        if (dryRun || plan.IsNoOp) return result;

        // Refuse if any item would lose every genre it has. Nothing in the current
        // data triggers this, but a future provider quirk could, and silently
        // stripping an item's whole taxonomy is worse than doing nothing.
        if (plan.ItemsLeftWithNoGenres > 0)
        {
            _logger.LogError(
                "Genre normalisation aborted: {Count} item(s) would be left with no genres.",
                plan.ItemsLeftWithNoGenres);
            throw new InvalidOperationException(
                $"Normalisation would strip all genres from {plan.ItemsLeftWithNoGenres} item(s); aborted.");
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Write order matters. Links go first (they FK the rows about to die), then
        // retire the losers, and only then rename survivors — renaming earlier could
        // collide with a case-variant row still present, which the UNIQUE index on
        // Name would reject.
        _db.MediaItemGenres.RemoveRange(linkEntities);
        await _db.SaveChangesAsync(ct);

        var retiredSet = plan.RetiredGenreIds.ToHashSet();
        _db.Genres.RemoveRange(genreEntities.Where(g => retiredSet.Contains(g.Id)));

        var createdByName = new Dictionary<string, Genre>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, targetId) in plan.TargetIdByName.Where(kv => kv.Value == null))
        {
            var fresh = new Genre { Name = name };
            createdByName[name] = fresh;
            _db.Genres.Add(fresh);
        }
        await _db.SaveChangesAsync(ct);

        var survivorsById = genreEntities.ToDictionary(g => g.Id);
        foreach (var (name, targetId) in plan.TargetIdByName)
        {
            if (targetId is not int id) continue;
            var row = survivorsById[id];
            if (!string.Equals(row.Name, name, StringComparison.Ordinal)) row.Name = name;
        }
        await _db.SaveChangesAsync(ct);

        int IdFor(string name) =>
            plan.TargetIdByName[name] ?? createdByName[name].Id;

        var rebuilt = plan.DesiredLinksByItem
            .SelectMany(kv => kv.Value.Select(name => new MediaItemGenre
            {
                MediaItemId = kv.Key,
                GenreId = IdFor(name),
            }))
            .ToList();
        _db.MediaItemGenres.AddRange(rebuilt);
        await _db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Genre normalisation: {Before} -> {After} genres ({Retired} retired, {Created} from splits, "
            + "{Dropped} dropped); links {LinksBefore} -> {LinksAfter}",
            result.GenresBefore, result.GenresAfter, result.GenresRetired,
            result.GenresCreatedFromSplits, result.GenresDropped, result.LinksBefore, result.LinksAfter);

        return result;
    }

    /// <summary>A readable sample of what changes, so a dry run is reviewable.</summary>
    private static List<string> BuildExamples(List<Genre> genres)
    {
        var expansion = genres.ToDictionary(g => g.Id, g => GenreNormalizer.Normalize(g.Name).ToList());
        var examples = new List<string>();

        foreach (var group in genres
            .Where(g => expansion[g.Id].Count == 1)
            .GroupBy(g => expansion[g.Id][0], StringComparer.OrdinalIgnoreCase)
            .Where(grp => grp.Count() > 1)
            .OrderByDescending(grp => grp.Count())
            .Take(10))
        {
            examples.Add($"merge: {group.Key} <= {string.Join(" | ", group.Select(g => $"\"{g.Name}\""))}");
        }

        foreach (var g in genres.Where(g => expansion[g.Id].Count > 1).Take(5))
            examples.Add($"split: \"{g.Name}\" -> {string.Join(" + ", expansion[g.Id])}");

        foreach (var g in genres.Where(g => expansion[g.Id].Count == 0).Take(5))
            examples.Add($"drop:  \"{g.Name}\"");

        return examples;
    }
}
