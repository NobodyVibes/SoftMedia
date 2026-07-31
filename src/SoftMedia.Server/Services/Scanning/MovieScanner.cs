using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Metadata;

namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Scanner for movie libraries. Flat structure with metadata enrichment.
/// </summary>
public class MovieScanner : BaseMediaScanner
{
    private readonly IMediaAnalysisService _mediaAnalysisService;
    private readonly ILocalArtworkService _localArtwork;

    public override LibraryType SupportedType => LibraryType.Movie;
    public override string[] SupportedExtensions => SoftMedia.Server.Constants.MediaExtensions.Video;
    public override string DisplayName => "Movie Scanner";

    public MovieScanner(
        IServiceScopeFactory scopeFactory,
        ILogger<MovieScanner> logger,
        IMediaNotificationService notificationService,
        IMediaAnalysisService mediaAnalysisService,
        IMetadataQueue metadataQueue,
        ILocalArtworkService localArtwork)
        : base(scopeFactory, logger, notificationService, metadataQueue)
    {
        _mediaAnalysisService = mediaAnalysisService;
        _localArtwork = localArtwork;
    }

    /// <summary>
    /// DV-WI-010: after the parallel walk completes, one library-wide grouping pass
    /// converges duplicate copies that were first seen by DIFFERENT workers in this scan
    /// (their contexts couldn't see each other's unsaved rows). Failure-isolated — the
    /// boot backfill heals anything this pass misses.
    /// </summary>
    public override async Task ScanLibraryAsync(
        Library library,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await base.ScanLibraryAsync(library, progress, cancellationToken);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var changed = await VersionGroupAssigner.GroupMoviesAsync(db, library.Id, cancellationToken);
            if (changed > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("[MovieScanner] Version-group pass grouped {Count} movie row(s).", changed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MovieScanner] Version-group pass failed; the boot backfill will heal it.");
        }
    }

    /// <summary>
    /// Process a single video file as a movie.
    /// </summary>
    protected override async Task<ScanOperationResult> ProcessFileAsync(
        AppDbContext context,
        FileDiscoveryResult file,
        MediaItem? existing,
        Library library,
        CancellationToken cancellationToken)
    {
        var filePath = file.Path;
        try
        {
            // Parse title and year from filename
            var parsed = FileNameParser.ParseMovie(filePath);
            var title = parsed.Title;
            var year = parsed.Year;

            if (string.IsNullOrEmpty(title))
                title = Path.GetFileNameWithoutExtension(filePath);

            // Create or update movie. Capture whether the file actually changed BEFORE
            // stamping Size/DateModified — the analysis strategy compares DateModified
            // against the file's mtime, so stamping first silently disabled re-probes
            // of modified files.
            var isNew = existing == null;
            var fileChanged = existing != null
                && (existing.Size != file.Size || existing.DateModified != file.LastWriteUtc);
            var movie = existing ?? new MediaItem { LibraryId = library.Id };

            // SM-WI-010: identity fields (Title/SortTitle/Year) come from the filename
            // only when the row is NEW. Existing rows are matched by Path, so the parse
            // cannot differ from creation time — re-stamping could only revert provider
            // enrichment or admin edits (it wiped enriched Years for yearless filenames
            // and undid Fix-Match corrections despite MetadataLocked). Year is fill-only
            // for unlocked existing rows: a parse may fill a hole, never overwrite.
            if (isNew)
            {
                movie.Title = title;
                movie.SortTitle = MediaStringHelpers.GetSortTitle(title);
                movie.Year = year;
            }
            else if (!movie.MetadataLocked)
            {
                movie.Year ??= year;
            }
            movie.Path = filePath;
            movie.Type = MediaType.Movie;
            movie.Size = file.Size;
            movie.DateModified = file.LastWriteUtc;

            // Delegate technical analysis to MediaAnalysisService (Smart Probe).
            // Changed files get a full re-probe; unchanged files stay in Missing mode,
            // which only probes when technical data is absent.
            var refreshMode = isNew || fileChanged ? MetadataRefreshMode.Full : MetadataRefreshMode.Missing;
            await _mediaAnalysisService.AnalyzeAsync(movie, filePath, refreshMode, cancellationToken);

            // R-WI-014: local sidecar artwork (poster.jpg / folder.jpg / <stem>-poster.* beside
            // the movie) wins over provider art. Runs every scan so added/updated/removed
            // sidecars are picked up; a removed local poster forces re-enrichment so provider
            // art returns. Enrichment still happens for local-art items (the policy treats a
            // local-only poster as incomplete until one pass stamps MetadataHash).
            var artwork = await _localArtwork.ApplyLocalArtworkAsync(
                movie, Path.GetDirectoryName(filePath) ?? string.Empty, Path.GetFileNameWithoutExtension(filePath),
                GetCachedDirectoryListing); // SM-WI-051: one listing per directory per scan

            if (isNew)
            {
                // Assign ID early if needed for queue? 
                // Entity Framework generates ID on Add? No, usually Guid is generated by client or constructor.
                // MediaItem constructor generates Id? Check Source. 
                // If not, we should generate it.
                if (movie.Id == Guid.Empty) movie.Id = Guid.NewGuid();

                // DV-WI-010: group with an already-persisted same-identity copy (covers
                // watcher imports and incremental scans; copies first seen in the SAME
                // parallel scan converge in the post-scan GroupMoviesAsync pass instead).
                await VersionGroupAssigner.AssignMovieGroupAsync(context, movie, cancellationToken);

                context.MediaItems.Add(movie);

                _logger.LogDebug("[MovieScanner] Added movie: {Title} ({Year})", title, year);
                return new ScanOperationResult(ScanResult.New, movie.Id, EnqueueMetadata: true);
            }
            else
            {
                // Check if metadata needs refresh. A removed local poster forces a re-enqueue
                // even though the policy alone wouldn't (the item may still look "complete"),
                // so provider art comes back promptly.
                var needsEnrichment = artwork.LocalPosterRemoved
                    || MetadataEnrichmentPolicy.NeedsEnrichment(existing!, _strictEnrichment);

                if (needsEnrichment)
                {
                    _logger.LogDebug("[MovieScanner] Queued metadata enrichment: {Title}", title);
                    return new ScanOperationResult(ScanResult.Updated, movie.Id, EnqueueMetadata: true);
                }
                else
                {
                    // Unchanged file + complete metadata = skipped, so scan counters
                    // reflect reality instead of reporting every rescan as "updated".
                    _logger.LogDebug("[MovieScanner] {Action} movie: {Title}", fileChanged ? "Updated" : "Skipped", title);
                    return new ScanOperationResult(
                        fileChanged ? ScanResult.Updated : ScanResult.Skipped, movie.Id, EnqueueMetadata: false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MovieScanner] Error processing file: {FilePath}", filePath);
            return new ScanOperationResult(ScanResult.Skipped);
        }
    }
}
