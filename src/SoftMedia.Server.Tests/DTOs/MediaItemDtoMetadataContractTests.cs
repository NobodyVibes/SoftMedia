using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using Xunit;

namespace SoftMedia.Server.Tests.DTOs;

/// <summary>
/// SR-WI-063 canary — <see cref="MediaItemDto.Metadata"/> is a FROZEN contract.
/// The only keys the server may emit are the R-WI-017 name-context keys
/// (artist/album/seriesTitle) and, for Photo items, the PhotoExifReader display
/// fields (camera/iso/fstop/exposure/dateTaken/gps). If one of these tests fails
/// because a new key appeared, that key needs a plan-recorded decision FIRST —
/// then both the XML docs on the property and this frozen set get updated.
/// </summary>
public class MediaItemDtoMetadataContractTests
{
    private static readonly HashSet<string> NameContextKeys = new()
    {
        "artist", "album", "seriesTitle"
    };

    private static readonly HashSet<string> PhotoExifKeys = new()
    {
        "camera", "iso", "fstop", "exposure", "dateTaken", "gps"
    };

    private static MediaItem NewItem(MediaType type) => new()
    {
        Id = Guid.NewGuid(),
        LibraryId = Guid.NewGuid(),
        Title = "Item",
        SortTitle = "Item",
        Path = "/lib/item.bin",
        DateAdded = DateTime.UtcNow,
        Type = type
    };

    [Fact]
    public void AudioTrack_WithArtistAndAlbumLoaded_EmitsOnlyNameContextKeys()
    {
        var item = NewItem(MediaType.Audio);
        item.Artist = new MediaItem { Title = "The Band", SortTitle = "Band", Path = "/m/band", Type = MediaType.Artist };
        item.Album = new MediaItem { Title = "The Album", SortTitle = "Album", Path = "/m/band/album", Type = MediaType.Album };

        var dto = MediaItemDto.FromMediaItem(item);

        Assert.NotNull(dto.Metadata);
        Assert.True(dto.Metadata!.Keys.All(NameContextKeys.Contains),
            $"Undocumented Metadata key(s) on Audio: {string.Join(", ", dto.Metadata.Keys.Where(k => !NameContextKeys.Contains(k)))}");
    }

    [Fact]
    public void Episode_WithSeriesLoaded_EmitsOnlyNameContextKeys()
    {
        var item = NewItem(MediaType.Episode);
        item.Series = new MediaItem { Title = "The Show", SortTitle = "Show", Path = "/tv/show", Type = MediaType.Series };

        var dto = MediaItemDto.FromMediaItem(item);

        Assert.NotNull(dto.Metadata);
        Assert.True(dto.Metadata!.Keys.All(NameContextKeys.Contains),
            $"Undocumented Metadata key(s) on Episode: {string.Join(", ", dto.Metadata.Keys.Where(k => !NameContextKeys.Contains(k)))}");
    }

    [Fact]
    public void Photo_WithFullExifJson_EmitsOnlyDocumentedExifKeys()
    {
        var item = NewItem(MediaType.Photo);
        item.ExifJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["camera"] = "Canon EOS R5",
            ["iso"] = "200",
            ["fstop"] = "f/2.8",
            ["exposure"] = "1/250",
            ["dateTaken"] = "2025-06-01 14:00:00",
            ["gps"] = "12.34, 56.78"
        });

        var dto = MediaItemDto.FromMediaItem(item);

        var allowed = new HashSet<string>(PhotoExifKeys.Concat(NameContextKeys));
        Assert.NotNull(dto.Metadata);
        Assert.True(dto.Metadata!.Keys.All(allowed.Contains),
            $"Undocumented Metadata key(s) on Photo: {string.Join(", ", dto.Metadata.Keys.Where(k => !allowed.Contains(k)))}");
    }

    [Fact]
    public void Movie_WithoutNavigationsOrExif_EmitsNoMetadata()
    {
        var dto = MediaItemDto.FromMediaItem(NewItem(MediaType.Movie));

        Assert.Null(dto.Metadata);
    }
}
