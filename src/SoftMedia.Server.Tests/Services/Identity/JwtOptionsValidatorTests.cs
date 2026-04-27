using Microsoft.Extensions.Configuration;
using SoftMedia.Server.Models.Options;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Identity;

public class JwtOptionsValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateSecret_Missing_Fails(string? secret)
    {
        var result = JwtOptionsValidator.ValidateSecret(secret);
        Assert.False(result.IsValid);
        Assert.Contains("not configured", result.ErrorMessage);
    }

    [Fact]
    public void ValidateSecret_Placeholder_Fails()
    {
        var placeholder = JwtOptions.BlockedSecrets[0];
        var result = JwtOptionsValidator.ValidateSecret(placeholder);
        Assert.False(result.IsValid);
        Assert.Contains("committed placeholder", result.ErrorMessage);
    }

    [Fact]
    public void ValidateSecret_TooShort_Fails()
    {
        // 31 ASCII bytes — below the 32-byte minimum
        var result = JwtOptionsValidator.ValidateSecret(new string('x', 31));
        Assert.False(result.IsValid);
        Assert.Contains("too short", result.ErrorMessage);
    }

    [Fact]
    public void ValidateSecret_ExactMinimum_Succeeds()
    {
        // 32 ASCII bytes — right at the floor
        var result = JwtOptionsValidator.ValidateSecret(new string('x', 32));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateSecret_Strong_Succeeds()
    {
        var result = JwtOptionsValidator.ValidateSecret(JwtSecretGenerator.Generate());
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_ReadsFromJwtSettingsSection()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JwtSettings:Secret"] = JwtSecretGenerator.Generate()
            })
            .Build();

        var result = JwtOptionsValidator.Validate(config);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithMissingSection_Fails()
    {
        var config = new ConfigurationBuilder().Build();
        var result = JwtOptionsValidator.Validate(config);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Generate_ProducesDistinctSecrets()
    {
        var a = JwtSecretGenerator.Generate();
        var b = JwtSecretGenerator.Generate();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Generate_ProducesUrlSafeString()
    {
        // Base64url uses A-Z a-z 0-9 - _ only; no '+' '/' '='
        var secret = JwtSecretGenerator.Generate();
        Assert.DoesNotContain('+', secret);
        Assert.DoesNotContain('/', secret);
        Assert.DoesNotContain('=', secret);
    }

    [Fact]
    public void Generate_ProducesSecretAboveMinimum()
    {
        var secret = JwtSecretGenerator.Generate();
        var byteLength = System.Text.Encoding.UTF8.GetByteCount(secret);
        Assert.True(byteLength >= JwtOptions.MinimumSecretByteLength);
    }
}
