using System.Text;
using System.Xml;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Security.ContentRating;

namespace SoftMedia.Server.Services.Dlna;

/// <summary>Result of a ContentDirectory Browse: the (un-escaped) DIDL-Lite document plus counts.</summary>
public record DlnaBrowseResult(string Didl, int NumberReturned, int TotalMatches);

public interface IDlnaContentDirectory
{
    /// <param name="objectId">"0" = root; "L:{lib}", "S:{series}", "A:{album}", "I:{item}".</param>
    /// <param name="metadata">true = BrowseMetadata (the object itself); false = BrowseDirectChildren.</param>
    /// <param name="resBaseUrl">Absolute origin the TV reached us on, e.g. "http://192.168.1.50:5011".</param>
    Task<DlnaBrowseResult> BrowseAsync(string objectId, bool metadata, int startingIndex, int requestedCount, string resBaseUrl, CancellationToken ct);
}

/// <summary>
/// Maps SoftMedia's library/media tree onto a UPnP ContentDirectory (DLNA DMS). Only the
/// audio/video libraries are exposed — a TV media player can't open books/games/photos.
/// Hierarchy: root → AV libraries → { movies | series → episodes | albums → tracks }.
/// </summary>
public class DlnaContentDirectory : IDlnaContentDirectory
{
    private readonly AppDbContext _db;
    private readonly ISettingsService _settings;
    public DlnaContentDirectory(AppDbContext db, ISettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    private static readonly LibraryType[] AvLibraries = { LibraryType.Movie, LibraryType.TV, LibraryType.Music };

    /// Audit M7/L9: the admin-configured set of libraries exposed over DLNA (empty = none).
    private async Task<List<Guid>> ExposedLibraryIdsAsync(CancellationToken ct)
        => DlnaAccess.ParseExposedLibraryIds(await _settings.GetSettingAsync(DlnaAccess.ExposedLibrariesSetting, ""));

    /// Audit wave-2 M-6: the admin-configured per-type rating ceiling for DLNA (empty = no cap).
    /// Built from the same per-type JSON the user ceiling uses so the exact same EF predicate gates
    /// both surfaces.
    private async Task<UserRatingCeilings> DlnaCeilingsAsync(CancellationToken ct)
    {
        var json = await _settings.GetSettingAsync(DlnaAccess.MaxContentRatingsSetting, "");
        if (string.IsNullOrWhiteSpace(json)) return UserRatingCeilings.Unrestricted;
        return UserRatingCeilings.From(new Models.User { MaxRating = "", ContentRatings = json });
    }

    /// DLNA Browse page-size ceiling (audit wave-2 L-15) — RequestedCount=0 means "all" in UPnP,
    /// which previously mapped to Take(int.MaxValue) over MediaItems. Bound it.
    private const int DlnaMaxPageSize = 1000;

    private const string DidlNs = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
    private const string DcNs = "http://purl.org/dc/elements/1.1/";
    private const string UpnpNs = "urn:schemas-upnp-org:metadata-1-0/upnp/";
    private const string DlnaNs = "urn:schemas-dlna-org:metadata-1-0/";
    private const string XmlnsNs = "http://www.w3.org/2000/xmlns/";

    public async Task<DlnaBrowseResult> BrowseAsync(string objectId, bool metadata, int startingIndex, int requestedCount, string resBaseUrl, CancellationToken ct)
    {
        // Audit wave-2 L-15: clamp the page size (RequestedCount=0 = "all" must not be unbounded).
        var take = requestedCount <= 0 ? DlnaMaxPageSize : Math.Min(requestedCount, DlnaMaxPageSize);
        var skip = Math.Max(0, startingIndex);

        // Audit M7/L9: only libraries the admin exposed are visible over DLNA (empty = none).
        var exposed = await ExposedLibraryIdsAsync(ct);

        if (metadata)
            return await BrowseMetadataAsync(objectId, exposed, resBaseUrl, ct);

        var (kind, key) = ParseId(objectId);
        return kind switch
        {
            "0" => await RootChildrenAsync(exposed, skip, take, ct),
            "L" => await LibraryChildrenAsync(Guid.Parse(key), exposed, skip, take, resBaseUrl, ct),
            "S" => await SeriesChildrenAsync(Guid.Parse(key), exposed, skip, take, resBaseUrl, ct),
            "A" => await AlbumChildrenAsync(Guid.Parse(key), exposed, skip, take, resBaseUrl, ct),
            _ => Empty(),
        };
    }

    // --- Children listings -------------------------------------------------

    private async Task<DlnaBrowseResult> RootChildrenAsync(List<Guid> exposed, int skip, int take, CancellationToken ct)
    {
        var libs = await _db.Libraries
            .Where(l => AvLibraries.Contains(l.Type) && exposed.Contains(l.Id))
            .OrderBy(l => l.Order).ThenBy(l => l.Name)
            .ToListAsync(ct);

        var page = libs.Skip(skip).Take(take).ToList();
        var sb = new StringBuilder();
        using (var w = OpenDidl(sb))
        {
            foreach (var lib in page)
                WriteContainer(w, $"L:{lib.Id}", "0", lib.Name, await LibraryChildCountAsync(lib, ct));
        }
        return new DlnaBrowseResult(Close(sb), page.Count, libs.Count);
    }

    private async Task<DlnaBrowseResult> LibraryChildrenAsync(Guid libraryId, List<Guid> exposed, int skip, int take, string resBaseUrl, CancellationToken ct)
    {
        // Audit M7: a library the admin did not expose is invisible even if its id is guessed.
        if (!exposed.Contains(libraryId)) return Empty();

        var lib = await _db.Libraries.FirstOrDefaultAsync(l => l.Id == libraryId, ct);
        if (lib == null) return Empty();

        IQueryable<MediaItem> q = lib.Type switch
        {
            LibraryType.Movie => _db.MediaItems.Where(m => m.LibraryId == libraryId && m.Type == MediaType.Movie).OrderBy(m => m.SortTitle),
            LibraryType.TV => _db.MediaItems.Where(m => m.LibraryId == libraryId && m.Type == MediaType.Series).OrderBy(m => m.SortTitle),
            LibraryType.Music => _db.MediaItems.Where(m => m.LibraryId == libraryId && m.Type == MediaType.Album).OrderBy(m => m.SortTitle),
            _ => _db.MediaItems.Where(_ => false),
        };

        // Audit wave-2 M-6: apply the DLNA rating ceiling (gates Movie/Series; Music is ungated).
        q = q.ApplyContentRatingFilter(await DlnaCeilingsAsync(ct));

        var total = await q.CountAsync(ct);
        var page = await q.Skip(skip).Take(take).ToListAsync(ct);

        var sb = new StringBuilder();
        using (var w = OpenDidl(sb))
        {
            foreach (var item in page)
            {
                switch (lib.Type)
                {
                    case LibraryType.Movie:
                        WriteVideoItem(w, item, $"L:{libraryId}", resBaseUrl);
                        break;
                    case LibraryType.TV:
                        WriteContainer(w, $"S:{item.Id}", $"L:{libraryId}", item.Title, await _db.MediaItems.CountAsync(m => m.SeriesId == item.Id && m.Type == MediaType.Episode, ct), "object.container.album.videoAlbum");
                        break;
                    case LibraryType.Music:
                        WriteContainer(w, $"A:{item.Id}", $"L:{libraryId}", item.Title, await _db.MediaItems.CountAsync(m => m.AlbumId == item.Id && m.Type == MediaType.Audio, ct), "object.container.album.musicAlbum");
                        break;
                }
            }
        }
        return new DlnaBrowseResult(Close(sb), page.Count, total);
    }

    private async Task<DlnaBrowseResult> SeriesChildrenAsync(Guid seriesId, List<Guid> exposed, int skip, int take, string resBaseUrl, CancellationToken ct)
    {
        // Audit M7: only episodes whose library is exposed are listed (guards a guessed series id).
        var q = _db.MediaItems.Where(m => m.SeriesId == seriesId && m.Type == MediaType.Episode && exposed.Contains(m.LibraryId))
            .OrderBy(m => m.SeasonNumber).ThenBy(m => m.EpisodeNumber).ThenBy(m => m.SortTitle)
            .ApplyContentRatingFilter(await DlnaCeilingsAsync(ct)); // audit wave-2 M-6
        var total = await q.CountAsync(ct);
        var page = await q.Skip(skip).Take(take).ToListAsync(ct);

        var sb = new StringBuilder();
        using (var w = OpenDidl(sb))
            foreach (var ep in page) WriteVideoItem(w, ep, $"S:{seriesId}", resBaseUrl);
        return new DlnaBrowseResult(Close(sb), page.Count, total);
    }

    private async Task<DlnaBrowseResult> AlbumChildrenAsync(Guid albumId, List<Guid> exposed, int skip, int take, string resBaseUrl, CancellationToken ct)
    {
        // Audit M7: only tracks whose library is exposed are listed (guards a guessed album id).
        var q = _db.MediaItems.Where(m => m.AlbumId == albumId && m.Type == MediaType.Audio && exposed.Contains(m.LibraryId))
            .OrderBy(m => m.DiscNumber).ThenBy(m => m.TrackNumber).ThenBy(m => m.SortTitle);
        var total = await q.CountAsync(ct);
        var page = await q.Skip(skip).Take(take).ToListAsync(ct);

        var sb = new StringBuilder();
        using (var w = OpenDidl(sb))
            foreach (var track in page) WriteAudioItem(w, track, $"A:{albumId}", resBaseUrl);
        return new DlnaBrowseResult(Close(sb), page.Count, total);
    }

    // --- Metadata (the object itself) -------------------------------------

    private async Task<DlnaBrowseResult> BrowseMetadataAsync(string objectId, List<Guid> exposed, string resBaseUrl, CancellationToken ct)
    {
        var (kind, key) = ParseId(objectId);
        var sb = new StringBuilder();
        using (var w = OpenDidl(sb))
        {
            switch (kind)
            {
                case "0":
                    WriteContainer(w, "0", "-1", "SoftMedia", await _db.Libraries.CountAsync(l => AvLibraries.Contains(l.Type) && exposed.Contains(l.Id), ct));
                    break;
                case "L":
                    {
                        // Audit M7: don't reveal metadata for a non-exposed library.
                        var libId = Guid.Parse(key);
                        var lib = exposed.Contains(libId)
                            ? await _db.Libraries.FirstOrDefaultAsync(l => l.Id == libId, ct)
                            : null;
                        if (lib != null) WriteContainer(w, objectId, "0", lib.Name, await LibraryChildCountAsync(lib, ct));
                        break;
                    }
                case "I":
                    {
                        var item = await _db.MediaItems.FirstOrDefaultAsync(m => m.Id == Guid.Parse(key), ct);
                        // Audit M7/L9: only stream-able items in an exposed library. Audit wave-2
                        // M-6: and only within the DLNA rating ceiling.
                        if (item != null && exposed.Contains(item.LibraryId) && DlnaAccess.IsStreamableType(item.Type)
                            && RatingFilterExtensions.IsRatingAllowed(await DlnaCeilingsAsync(ct), item.Type, item.ContentRating))
                        {
                            if (item.Type == MediaType.Audio) WriteAudioItem(w, item, "0", resBaseUrl);
                            else WriteVideoItem(w, item, "0", resBaseUrl);
                        }
                        break;
                    }
                default:
                    WriteContainer(w, objectId, "0", objectId, 0);
                    break;
            }
        }
        return new DlnaBrowseResult(Close(sb), 1, 1);
    }

    private async Task<int> LibraryChildCountAsync(Library lib, CancellationToken ct) => lib.Type switch
    {
        LibraryType.Movie => await _db.MediaItems.CountAsync(m => m.LibraryId == lib.Id && m.Type == MediaType.Movie, ct),
        LibraryType.TV => await _db.MediaItems.CountAsync(m => m.LibraryId == lib.Id && m.Type == MediaType.Series, ct),
        LibraryType.Music => await _db.MediaItems.CountAsync(m => m.LibraryId == lib.Id && m.Type == MediaType.Album, ct),
        _ => 0,
    };

    // --- DIDL-Lite writing (XmlWriter handles all escaping) ----------------

    private static XmlWriter OpenDidl(StringBuilder sb)
    {
        var w = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false, ConformanceLevel = ConformanceLevel.Fragment });
        // Declare the exact dc/upnp/dlna prefixes — some TV DLNA parsers key on the literal
        // prefix, not the namespace URI. XmlWriter then reuses them and escapes all text.
        w.WriteStartElement(null, "DIDL-Lite", DidlNs);
        w.WriteAttributeString("xmlns", "dc", XmlnsNs, DcNs);
        w.WriteAttributeString("xmlns", "upnp", XmlnsNs, UpnpNs);
        w.WriteAttributeString("xmlns", "dlna", XmlnsNs, DlnaNs);
        return w;
    }

    private static string Close(StringBuilder sb) => sb.ToString();

    private static void WriteContainer(XmlWriter w, string id, string parentId, string title, int childCount, string upnpClass = "object.container.storageFolder")
    {
        w.WriteStartElement("container", DidlNs);
        w.WriteAttributeString("id", id);
        w.WriteAttributeString("parentID", parentId);
        w.WriteAttributeString("restricted", "1");
        w.WriteAttributeString("childCount", childCount.ToString());
        w.WriteElementString("dc", "title", DcNs, string.IsNullOrEmpty(title) ? "Untitled" : title);
        w.WriteElementString("upnp", "class", UpnpNs, upnpClass);
        w.WriteEndElement();
    }

    private void WriteVideoItem(XmlWriter w, MediaItem item, string parentId, string resBaseUrl)
        => WriteItem(w, item, parentId, resBaseUrl, "object.item.videoItem", DlnaProtocol.VideoFlags);

    private void WriteAudioItem(XmlWriter w, MediaItem item, string parentId, string resBaseUrl)
        => WriteItem(w, item, parentId, resBaseUrl, "object.item.audioItem.musicTrack", DlnaProtocol.AudioFlags);

    private void WriteItem(XmlWriter w, MediaItem item, string parentId, string resBaseUrl, string upnpClass, string dlnaFlags)
    {
        var mime = MimeTypeResolver.GetMimeType(item.Path);
        w.WriteStartElement("item", DidlNs);
        w.WriteAttributeString("id", $"I:{item.Id}");
        w.WriteAttributeString("parentID", parentId);
        w.WriteAttributeString("restricted", "1");
        w.WriteElementString("dc", "title", DcNs, string.IsNullOrEmpty(item.Title) ? "Untitled" : item.Title);
        w.WriteElementString("upnp", "class", UpnpNs, upnpClass);

        w.WriteStartElement("res", DidlNs);
        w.WriteAttributeString("protocolInfo", $"http-get:*:{mime}:{dlnaFlags}");
        if (item.Size > 0) w.WriteAttributeString("size", item.Size.ToString());
        if (item.Duration > 0) w.WriteAttributeString("duration", FormatDuration(item.Duration));
        if (item.Bitrate is > 0) w.WriteAttributeString("bitrate", ((long)(item.Bitrate.Value / 8)).ToString()); // DLNA = bytes/sec
        if (item.Width is > 0 && item.Height is > 0) w.WriteAttributeString("resolution", $"{item.Width}x{item.Height}");
        w.WriteString($"{resBaseUrl}/dlna/media/{item.Id}");
        w.WriteEndElement(); // res

        w.WriteEndElement(); // item
    }

    private static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    private static (string kind, string key) ParseId(string objectId)
    {
        if (string.IsNullOrEmpty(objectId) || objectId == "0") return ("0", "");
        var idx = objectId.IndexOf(':');
        return idx < 0 ? (objectId, "") : (objectId[..idx], objectId[(idx + 1)..]);
    }

    private static DlnaBrowseResult Empty()
    {
        var sb = new StringBuilder();
        using (OpenDidl(sb)) { }
        return new DlnaBrowseResult(sb.ToString(), 0, 0);
    }
}
