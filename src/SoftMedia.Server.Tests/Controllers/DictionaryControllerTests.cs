using Microsoft.AspNetCore.Mvc;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Services.Abstractions;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

public class DictionaryControllerTests
{
    private readonly Mock<IDictionaryService> _svc = new();

    private DictionaryController NewController() => new(_svc.Object);

    [Fact]
    public async Task Lookup_Returns501WhenDictionaryUnavailable()
    {
        _svc.Setup(s => s.Available).Returns(false);

        var result = await NewController().Lookup("serendipity", CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(501, obj.StatusCode);
        var dto = Assert.IsType<DictionaryLookupResponse>(obj.Value);
        Assert.False(dto.Available);
    }

    [Fact]
    public async Task Lookup_ReturnsDefinitionsWhenAvailable()
    {
        _svc.Setup(s => s.Available).Returns(true);
        _svc.Setup(s => s.LookupAsync("serendipity", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "the occurrence of events by chance in a happy way" });

        var result = await NewController().Lookup("serendipity", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<DictionaryLookupResponse>(ok.Value);
        Assert.True(dto.Available);
        Assert.Single(dto.Definitions);
        Assert.Contains("happy way", dto.Definitions[0]);
    }

    [Fact]
    public async Task Lookup_ReturnsEmptyDefinitionsForUnknownWord()
    {
        _svc.Setup(s => s.Available).Returns(true);
        _svc.Setup(s => s.LookupAsync("xyzzy", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        var result = await NewController().Lookup("xyzzy", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<DictionaryLookupResponse>(ok.Value);
        Assert.True(dto.Available);
        Assert.Empty(dto.Definitions);
    }

    [Fact]
    public async Task Lookup_RejectsEmptyWord()
    {
        var result = await NewController().Lookup("   ", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }
}
