using System.Text.Json;
using SoftMedia.Server.Models;
using Xunit;

namespace SoftMedia.Tests;

public class MetadataResultTests
{
    [Fact]
    public void MetadataResult_ShouldSerializeAndDeserializeCorrectly_PreservingCamelCase()
    {
        // Arrange
        var result = new MetadataResult
        {
            Title = "Dune",
            Year = 2021,
            ImdbId = "tt1160419",
            PosterUrl = "http://example.com/dune.jpg",
            Genres = new List<string> { "Sci-Fi", "Adventure" },
            Cast = new List<CastMember>
            {
                new CastMember { Name = "Timothée Chalamet", Character = "Paul Atreides", Id = 123 }
            }
        };

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // Act
        var json = JsonSerializer.Serialize(result, options);
        var deserialized = JsonSerializer.Deserialize<MetadataResult>(json, options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal("Dune", deserialized.Title);
        Assert.Equal(2021, deserialized.Year);
        Assert.Equal("tt1160419", deserialized.ImdbId);
        Assert.Equal("http://example.com/dune.jpg", deserialized.PosterUrl);
        Assert.Contains("Sci-Fi", deserialized.Genres);
        Assert.Single(deserialized.Cast);
        Assert.Equal("Timothée Chalamet", deserialized.Cast[0].Name);
        Assert.Equal("Paul Atreides", deserialized.Cast[0].Character);
        Assert.Equal(123, deserialized.Cast[0].Id);
    }

    [Fact]
    public void MetadataResult_ShouldSupportJsonExtensionData_ForBackwardCompatibility()
    {
        // Arrange
        var json = @"
        {
            ""title"": ""Custom Movie"",
            ""year"": 2000,
            ""unknownLegacyField"": ""legacy_value"",
            ""anotherUnknown"": 42
        }";

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // Act
        var result = JsonSerializer.Deserialize<MetadataResult>(json, options);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Custom Movie", result.Title);
        Assert.Equal(2000, result.Year);
        Assert.NotNull(result.Extra);
        Assert.True(result.Extra.ContainsKey("unknownLegacyField"));
        Assert.Equal("legacy_value", result.Extra["unknownLegacyField"].GetString());
        Assert.Equal(42, result.Extra["anotherUnknown"].GetInt32());
    }
}
