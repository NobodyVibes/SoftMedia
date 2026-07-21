using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using Xunit;

namespace SoftMedia.Server.Tests.DTOs;

public class MediaItemDtoPhotoTests
{
    [Fact]
    public void FromMediaItem_Photo_MergesExifJsonIntoMetadata()
    {
        var item = new MediaItem
        {
            Type = MediaType.Photo,
            Title = "beach",
            Path = "C:/photos/beach.jpg",
            ExifJson = """{"camera":"Canon EOS R5","iso":"200","dateTaken":"2025-06-01 14:00:00"}""",
        };

        var dto = MediaItemDto.FromMediaItem(item);

        Assert.NotNull(dto.Metadata);
        Assert.Equal("Canon EOS R5", dto.Metadata!["camera"]);
        Assert.Equal("200", dto.Metadata["iso"]);
        Assert.Equal("2025-06-01 14:00:00", dto.Metadata["dateTaken"]);
    }

    [Fact]
    public void FromMediaItem_Photo_EmitsPhotosImageRouteAsPoster()
    {
        var item = new MediaItem { Type = MediaType.Photo, Title = "beach", Path = "C:/p/beach.jpg" };

        var dto = MediaItemDto.FromMediaItem(item);

        Assert.Equal($"/api/v1/photos/{item.Id}/image?width=480", dto.PosterPath);
    }

    [Fact]
    public void FromMediaItem_Photo_CorruptExifJson_DoesNotThrow()
    {
        var item = new MediaItem
        {
            Type = MediaType.Photo,
            Title = "beach",
            Path = "C:/p/beach.jpg",
            ExifJson = "{not valid json",
        };

        var dto = MediaItemDto.FromMediaItem(item);

        // The listing must survive a corrupt row; the photo just loses its EXIF cards.
        Assert.True(dto.Metadata == null || !dto.Metadata.ContainsKey("camera"));
    }

    [Fact]
    public void FromMediaItem_NonPhoto_IgnoresExifJson()
    {
        var item = new MediaItem
        {
            Type = MediaType.Movie,
            Title = "A Film",
            Path = "C:/m/film.mkv",
            ExifJson = """{"camera":"should not surface"}""",
        };

        var dto = MediaItemDto.FromMediaItem(item);

        Assert.True(dto.Metadata == null || !dto.Metadata.ContainsKey("camera"));
    }
}
