using System.Text.Json;
using SoftMedia.Server.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Helpers;

public class MetadataJsonMergerTests
{
    [Fact]
    public void Merge_WithNullValues_PreservesExistingData()
    {
        // Arrange — existing has a poster URL, incoming has null poster
        var existingJson = """{"poster":"http://example.com/img.jpg","title":"Test"}""";
        var incomingJson = """{"poster":null,"year":2024}""";

        var existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingJson)!;
        var incoming = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(incomingJson)!;

        // Act
        var result = MetadataJsonMerger.Merge(existing, incoming);

        // Assert — poster should NOT be overwritten by null
        Assert.Equal("http://example.com/img.jpg", result["poster"].GetString());
        Assert.Equal(2024, result["year"].GetInt32());
        Assert.Equal("Test", result["title"].GetString());
    }

    [Fact]
    public void Merge_WithValidValues_OverwritesExisting()
    {
        // Arrange
        var existingJson = """{"title":"Old Title","year":2020}""";
        var incomingJson = """{"title":"New Title","year":2024,"director":"Nolan"}""";

        var existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingJson)!;
        var incoming = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(incomingJson)!;

        // Act
        var result = MetadataJsonMerger.Merge(existing, incoming);

        // Assert — valid values should overwrite
        Assert.Equal("New Title", result["title"].GetString());
        Assert.Equal(2024, result["year"].GetInt32());
        Assert.Equal("Nolan", result["director"].GetString());
    }

    [Fact]
    public void Merge_EmptyIntoEmpty_ReturnsEmpty()
    {
        // Arrange
        var existing = new Dictionary<string, JsonElement>();
        var incoming = new Dictionary<string, JsonElement>();

        // Act
        var result = MetadataJsonMerger.Merge(existing, incoming);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void Merge_PreservesArraysAndObjects()
    {
        // Arrange — existing has a genres array
        var existingJson = """{"genres":["Action","Drama"],"rating":{"value":8.5}}""";
        var incomingJson = """{"year":2024}""";

        var existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingJson)!;
        var incoming = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(incomingJson)!;

        // Act
        var result = MetadataJsonMerger.Merge(existing, incoming);

        // Assert — arrays and objects should be preserved with full fidelity
        Assert.Equal(JsonValueKind.Array, result["genres"].ValueKind);
        Assert.Equal(2, result["genres"].GetArrayLength());
        Assert.Equal(JsonValueKind.Object, result["rating"].ValueKind);
        Assert.Equal(2024, result["year"].GetInt32());
    }

    [Fact]
    public void MergeJson_WithNullExisting_ReturnsIncomingData()
    {
        // Arrange
        var incomingJson = """{"title":"New","year":2024}""";

        // Act
        var result = MetadataJsonMerger.MergeJson(null, incomingJson);

        // Assert
        using var doc = JsonDocument.Parse(result);
        Assert.Equal("New", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(2024, doc.RootElement.GetProperty("year").GetInt32());
    }

    [Fact]
    public void MergeJson_MergesCorrectly()
    {
        // Arrange — existing has author from scanner, incoming has poster from provider
        var existingJson = """{"author":"Tolkien","scannedTags":true}""";
        var incomingJson = """{"poster":"http://covers.openlibrary.org/b/id/123-L.jpg","year":1954}""";

        // Act
        var result = MetadataJsonMerger.MergeJson(existingJson, incomingJson);

        // Assert — both scanner and provider fields should be present
        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.Equal("Tolkien", root.GetProperty("author").GetString());
        Assert.True(root.GetProperty("scannedTags").GetBoolean());
        Assert.Equal(1954, root.GetProperty("year").GetInt32());
        Assert.Contains("openlibrary", root.GetProperty("poster").GetString());
    }
}
