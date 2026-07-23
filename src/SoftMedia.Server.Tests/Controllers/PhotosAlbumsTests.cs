using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

/// Folder-derived photo albums: grouping by relative directory, root photos in
/// "Unsorted", chronological album contents, and the ACL/type anti-probe gates.
public class PhotosAlbumsTests
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"photo-albums-{Guid.NewGuid()}").Options);
    private readonly Mock<IUserLibraryAccessProvider> _access = new();
    private readonly Guid _libId = Guid.NewGuid();

    private PhotosController Build(bool unrestricted = true, params Guid[] allowed)
    {
        _access.Setup(a => a.GetCurrentAsync()).ReturnsAsync(
            unrestricted ? LibraryAccess.Unrestricted : LibraryAccess.AllowOnly(allowed));
        return new PhotosController(
            new Mock<IMediaRepository>().Object,
            new Mock<IStreamSecurityService>().Object,
            new Mock<IThumbnailService>().Object,
            _db, _access.Object, NullLogger<PhotosController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private async Task SeedAsync()
    {
        _db.Libraries.Add(new Library { Id = _libId, Name = "Photos", Type = LibraryType.Photo, Paths = new() { @"C:\photos" } });
        void Photo(string path, DateTime taken) => _db.MediaItems.Add(new MediaItem
        {
            Id = Guid.NewGuid(),
            LibraryId = _libId,
            Type = MediaType.Photo,
            Title = Path.GetFileNameWithoutExtension(path),
            SortTitle = Path.GetFileNameWithoutExtension(path),
            Path = path,
            ReleaseDate = taken,
            DateAdded = taken,
        });
        Photo(@"C:\photos\loose.jpg", new DateTime(2024, 1, 1));
        Photo(@"C:\photos\2024\Italy\colosseum.jpg", new DateTime(2024, 6, 2));
        Photo(@"C:\photos\2024\Italy\venice.jpg", new DateTime(2024, 6, 5));
        Photo(@"C:\photos\Pets\dog.jpg", new DateTime(2023, 3, 3));
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetAlbums_GroupsByFolder_NamesByLeaf_CoversNewest()
    {
        await SeedAsync();

        var result = await Build().GetAlbums(_libId);
        var albums = Assert.IsType<List<PhotoAlbumDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal(3, albums.Count);
        // Newest-first ordering: Italy (2024-06) > Unsorted (2024-01) > Pets (2023-03).
        Assert.Equal(new[] { "Italy", "Unsorted", "Pets" }, albums.Select(a => a.Name).ToArray());

        var italy = albums[0];
        Assert.Equal("2024/Italy", italy.Key);
        Assert.Equal(2, italy.PhotoCount);
        var venice = await _db.MediaItems.FirstAsync(m => m.Title == "venice");
        Assert.Equal(venice.Id, italy.CoverPhotoId); // cover = the album's newest photo
    }

    [Fact]
    public async Task GetAlbumPhotos_FiltersByKey_Chronological()
    {
        await SeedAsync();

        var result = await Build().GetAlbumPhotos(_libId, "2024/Italy");
        var photos = Assert.IsType<List<MediaItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal(new[] { "colosseum", "venice" }, photos.Select(p => p.Title).ToArray());
    }

    [Fact]
    public async Task GetAlbumPhotos_EmptyKey_IsTheRootAlbum()
    {
        await SeedAsync();

        var result = await Build().GetAlbumPhotos(_libId, null);
        var photos = Assert.IsType<List<MediaItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Single(photos);
        Assert.Equal("loose", photos[0].Title);
    }

    [Fact]
    public async Task AclDeniedOrWrongType_Is404_AntiProbe()
    {
        await SeedAsync();
        _db.Libraries.Add(new Library { Id = Guid.NewGuid(), Name = "Movies", Type = LibraryType.Movie, Paths = new() { @"C:\movies" } });
        await _db.SaveChangesAsync();
        var movieLib = await _db.Libraries.FirstAsync(l => l.Type == LibraryType.Movie);

        // ACL that does NOT include the photo library.
        var denied = Build(unrestricted: false, allowed: Guid.NewGuid());
        Assert.IsType<NotFoundResult>((await denied.GetAlbums(_libId)).Result);
        Assert.IsType<NotFoundResult>((await denied.GetAlbumPhotos(_libId, null)).Result);

        // A movie library must not answer on the photo-albums surface.
        Assert.IsType<NotFoundResult>((await Build().GetAlbums(movieLib.Id)).Result);
    }

    [Theory]
    [InlineData(@"C:\photos\a.jpg", "", "Unsorted")]
    [InlineData(@"C:\photos\Trip\a.jpg", "Trip", "Trip")]
    [InlineData(@"C:\photos\2024\Italy\a.jpg", "2024/Italy", "Italy")]
    [InlineData(@"D:\elsewhere\Orphans\a.jpg", "Orphans", "Orphans")] // edited-root fallback
    public void AlbumKeyAndName_DeriveFromRelativeFolder(string photoPath, string expectedKey, string expectedName)
    {
        var key = PhotosController.AlbumKeyFor(photoPath, new List<string> { @"C:\photos" });
        Assert.Equal(expectedKey, key);
        Assert.Equal(expectedName, PhotosController.AlbumNameFor(key));
    }
}
