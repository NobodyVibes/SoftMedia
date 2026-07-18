using System.Text.Json;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Security.ContentRating;

/// The rating ceilings in effect for a single request.
///
/// Built once per HTTP request from <see cref="User.MaxRating"/> (legacy single
/// string, used as the Movie ceiling fallback) and <see cref="User.ContentRatings"/>
/// (the per-type JSON map: e.g. `{"Movie":"PG-13","TV":"TV-14","Game":"T"}`).
///
/// Admins are represented as <see cref="Unrestricted"/> — that struct value
/// signals "no filtering at all" and short-circuits the IQueryable extension.
/// Background services (scanners) that don't have an HTTP context likewise
/// receive <see cref="Unrestricted"/> from the provider.
public readonly struct UserRatingCeilings
{
    /// Movie ceiling label (e.g. "PG-13"), or null when unrestricted for movies.
    public string? Movie { get; }

    /// TV ceiling label (e.g. "TV-14"), or null when unrestricted for TV.
    public string? Tv { get; }

    /// ESRB ceiling label (e.g. "T"), or null when unrestricted for games.
    public string? Game { get; }

    /// True when no filtering should apply (admin role, or no authenticated user).
    public bool IsUnrestricted { get; }

    private UserRatingCeilings(string? movie, string? tv, string? game, bool unrestricted)
    {
        Movie = movie;
        Tv = tv;
        Game = game;
        IsUnrestricted = unrestricted;
    }

    /// Sentinel meaning "no filtering applies" — admin or anonymous/scanner context.
    public static readonly UserRatingCeilings Unrestricted = new(null, null, null, true);

    /// Build the ceilings from a User row. The per-type ContentRatings JSON map
    /// wins when present; when a type has no entry there, MaxRating is used as
    /// the Movie fallback (legacy single-ceiling behaviour) and the other types
    /// are left null (unrestricted for those types).
    public static UserRatingCeilings From(User user)
    {
        var perType = ParseContentRatings(user.ContentRatings);

        // MaxRating is conventionally an MPAA label ("" = unrestricted — the model default
        // since R-WI-011; legacy rows may still carry "PG-13"). Use it as the Movie fallback
        // only — TV/Game stay unrestricted unless the per-type map names them explicitly.
        var movie = perType.GetValueOrDefault("Movie");
        if (string.IsNullOrWhiteSpace(movie))
        {
            movie = string.IsNullOrWhiteSpace(user.MaxRating) ? null : user.MaxRating;
        }

        return new UserRatingCeilings(
            movie: movie,
            tv: perType.GetValueOrDefault("TV"),
            game: perType.GetValueOrDefault("Game"),
            unrestricted: false);
    }

    private static IReadOnlyDictionary<string, string> ParseContentRatings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            // Malformed JSON in the User row — treat as empty rather than
            // failing the request. The user is then effectively gated by the
            // MaxRating fallback only.
            return new Dictionary<string, string>();
        }
    }
}
