using SoftMedia.Server.Services.Identity;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Identity;

/// Security audit L2: a minimum password policy so empty/trivial passwords are rejected.
public class PasswordPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]    // 5
    [InlineData("1234567")]  // 7 — one below the floor
    public void Rejects_TooShortOrEmpty(string? pw)
        => Assert.NotNull(PasswordPolicy.Validate(pw));

    [Theory]
    [InlineData("12345678")]    // 8 — exactly the floor
    [InlineData("correct horse battery staple")]
    public void Accepts_AtOrAboveFloor(string pw)
        => Assert.Null(PasswordPolicy.Validate(pw));
}
