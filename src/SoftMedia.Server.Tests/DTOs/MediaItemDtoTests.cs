using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using Xunit;

namespace SoftMedia.Server.Tests.DTOs;

public class MediaItemDtoTests
{
    private static MediaItem NewItem() => new()
    {
        Id = Guid.NewGuid(),
        LibraryId = Guid.NewGuid(),
        Title = "Test Show",
        SortTitle = "Test Show",
        Path = "/media/test",
        DateAdded = DateTime.UtcNow,
        Type = MediaType.Series
    };

    private static MediaItemCast NewCast(int order, Person person, string? character) => new()
    {
        PersonId = person.Id,
        Person = person,
        Character = character,
        Order = order
    };

    [Fact]
    public void FromMediaItem_ProjectsCast_OrderedByOrder()
    {
        var alice = new Person { Id = 1, Name = "Alice", ExternalId = 101 };
        var bob = new Person { Id = 2, Name = "Bob", ExternalId = 102 };

        var item = NewItem();
        item.MediaItemCasts = new List<MediaItemCast>
        {
            NewCast(order: 2, bob, "Bob Role"),
            NewCast(order: 0, alice, "Alice Role")
        };

        var dto = MediaItemDto.FromMediaItem(item);

        Assert.NotNull(dto.Cast);
        Assert.Equal(2, dto.Cast!.Count);
        Assert.Equal("Alice", dto.Cast[0].Name);
        Assert.Equal(0, dto.Cast[0].Order);
        Assert.Equal("Bob", dto.Cast[1].Name);
        Assert.Equal(2, dto.Cast[1].Order);
    }

    [Fact]
    public void FromMediaItem_SplitsMultipleCharacters_OnSlashDelimiter()
    {
        var person = new Person { Id = 1, Name = "Bryan Cranston" };
        var item = NewItem();
        item.MediaItemCasts = new List<MediaItemCast>
        {
            NewCast(0, person, "Walter White / Heisenberg / Mr. Lambert")
        };

        var dto = MediaItemDto.FromMediaItem(item);

        var characters = dto.Cast![0].Characters;
        Assert.Equal(3, characters.Count);
        Assert.Equal("Walter White", characters[0]);
        Assert.Equal("Heisenberg", characters[1]);
        Assert.Equal("Mr. Lambert", characters[2]);
    }

    [Fact]
    public void FromMediaItem_SingleCharacter_ProducesOneEntry()
    {
        var person = new Person { Id = 1, Name = "Solo Actor" };
        var item = NewItem();
        item.MediaItemCasts = new List<MediaItemCast>
        {
            NewCast(0, person, "Only Role")
        };

        var dto = MediaItemDto.FromMediaItem(item);

        Assert.Single(dto.Cast![0].Characters);
        Assert.Equal("Only Role", dto.Cast[0].Characters[0]);
    }

    [Fact]
    public void FromMediaItem_NullOrWhitespaceCharacter_ProducesEmptyList()
    {
        var person = new Person { Id = 1, Name = "Unknown Role Actor" };
        var item = NewItem();
        item.MediaItemCasts = new List<MediaItemCast>
        {
            NewCast(0, person, null),
            NewCast(1, new Person { Id = 2, Name = "Blank Actor" }, "   ")
        };

        var dto = MediaItemDto.FromMediaItem(item);

        Assert.Empty(dto.Cast![0].Characters);
        Assert.Empty(dto.Cast[1].Characters);
    }

    [Fact]
    public void FromMediaItem_ProxiesRemoteCastImageUrl()
    {
        var person = new Person
        {
            Id = 1,
            Name = "Actor",
            ImagePath = "https://static.tvmaze.com/uploads/images/medium_portrait/1/1234.jpg"
        };
        var item = NewItem();
        item.MediaItemCasts = new List<MediaItemCast> { NewCast(0, person, "Role") };

        var dto = MediaItemDto.FromMediaItem(item, imageProxyBaseUrl: "/api/v1/image/proxy");

        Assert.NotNull(dto.Cast![0].ImageUrl);
        Assert.StartsWith("/api/v1/image/proxy?url=", dto.Cast[0].ImageUrl);
        Assert.Contains(Uri.EscapeDataString(person.ImagePath), dto.Cast[0].ImageUrl!);
    }

    [Fact]
    public void FromMediaItem_PassesLocalCastImagePathThroughUnchanged()
    {
        var person = new Person
        {
            Id = 1,
            Name = "Actor",
            ImagePath = "/cache/images/tv/cast/1234.jpg"
        };
        var item = NewItem();
        item.MediaItemCasts = new List<MediaItemCast> { NewCast(0, person, "Role") };

        var dto = MediaItemDto.FromMediaItem(item, imageProxyBaseUrl: "/api/v1/image/proxy");

        Assert.Equal("/cache/images/tv/cast/1234.jpg", dto.Cast![0].ImageUrl);
    }

    [Fact]
    public void FromMediaItem_EmptyImagePath_YieldsNullImageUrl()
    {
        var person = new Person { Id = 1, Name = "Actor", ImagePath = null };
        var item = NewItem();
        item.MediaItemCasts = new List<MediaItemCast> { NewCast(0, person, "Role") };

        var dto = MediaItemDto.FromMediaItem(item, imageProxyBaseUrl: "/api/v1/image/proxy");

        Assert.Null(dto.Cast![0].ImageUrl);
    }

    [Fact]
    public void FromMediaItem_NoCastAssociations_LeavesCastNull()
    {
        var item = NewItem();
        item.MediaItemCasts = new List<MediaItemCast>();

        var dto = MediaItemDto.FromMediaItem(item);

        Assert.Null(dto.Cast);
    }

    [Fact]
    public void FromMediaItem_SkipsCastAssociationsWithNullPerson()
    {
        var validPerson = new Person { Id = 1, Name = "Alice" };
        var item = NewItem();
        item.MediaItemCasts = new List<MediaItemCast>
        {
            NewCast(0, validPerson, "Role"),
            new() { PersonId = 999, Person = null, Character = "Orphan", Order = 1 }
        };

        var dto = MediaItemDto.FromMediaItem(item);

        Assert.Single(dto.Cast!);
        Assert.Equal("Alice", dto.Cast![0].Name);
    }

    [Fact]
    public void FromMediaItem_ExposesExternalIdForDeepLinking()
    {
        var person = new Person { Id = 5, Name = "Actor", ExternalId = 9876 };
        var item = NewItem();
        item.MediaItemCasts = new List<MediaItemCast> { NewCast(0, person, "Role") };

        var dto = MediaItemDto.FromMediaItem(item);

        Assert.Equal(9876, dto.Cast![0].ExternalId);
        Assert.Equal(5, dto.Cast[0].Id);
    }

    [Fact]
    public void FromMediaItem_PropagatesIntroCreditsTimecodesAndSources()
    {
        var item = NewItem();
        item.IntroStart = 12.5;
        item.IntroEnd = 42.0;
        item.IntroSource = DetectionSource.Detected;
        item.CreditsStart = 1320.0;
        item.CreditsEnd = 1380.0;
        item.CreditsSource = DetectionSource.Chapter;

        var dto = MediaItemDto.FromMediaItem(item);

        Assert.Equal(12.5, dto.IntroStart);
        Assert.Equal(42.0, dto.IntroEnd);
        Assert.Equal(DetectionSource.Detected, dto.IntroSource);
        Assert.Equal(1320.0, dto.CreditsStart);
        Assert.Equal(1380.0, dto.CreditsEnd);
        Assert.Equal(DetectionSource.Chapter, dto.CreditsSource);
    }

    [Fact]
    public void FromMediaItem_LeavesIntroCreditsFieldsNull_WhenNotSet()
    {
        var item = NewItem();

        var dto = MediaItemDto.FromMediaItem(item);

        Assert.Null(dto.IntroStart);
        Assert.Null(dto.IntroEnd);
        Assert.Null(dto.IntroSource);
        Assert.Null(dto.CreditsStart);
        Assert.Null(dto.CreditsEnd);
        Assert.Null(dto.CreditsSource);
    }
}
