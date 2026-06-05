using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

/// Targeted tests for AuthController.Refresh. The most load-bearing branch is
/// reuse detection — presenting a rotated-away refresh token must revoke the
/// entire chain for that user, not just return 401. Full HTTP integration
/// tests covering every branch land with Todo 09.
public class AuthControllerRefreshTests
{
    [Fact]
    public async Task Refresh_ReuseDetected_CallsRevokeAllForUser_AndReturns401()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var stolenToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = "hash",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            ExpiresAt = DateTime.UtcNow.AddDays(6),
            RevokedAt = DateTime.UtcNow.AddMinutes(-30),
            ReplacedByTokenId = Guid.NewGuid(),
            ReasonRevoked = RefreshTokenRevocationReason.Rotated,
        };

        var refreshTokens = new Mock<IRefreshTokenService>();
        refreshTokens
            .Setup(s => s.ValidateAsync("stolen-raw", It.IsAny<CancellationToken>()))
            .ReturnsAsync(RefreshTokenValidationResult.ReuseDetected(stolenToken));

        var controller = BuildController(refreshTokens.Object);
        AttachCookie(controller, "refreshToken", "stolen-raw");

        // Act
        var result = await controller.Refresh();

        // Assert
        refreshTokens.Verify(s => s.RevokeAllForUserAsync(
            userId,
            RefreshTokenRevocationReason.ReuseDetected,
            It.IsAny<CancellationToken>()), Times.Once);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);

        // Normal-revoke path must NOT also run
        refreshTokens.Verify(s => s.RotateAsync(
            It.IsAny<RefreshToken>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Refresh_MissingCookie_Returns401_WithoutTouchingService()
    {
        var refreshTokens = new Mock<IRefreshTokenService>(MockBehavior.Strict);
        var controller = BuildController(refreshTokens.Object);
        // No cookie attached

        var result = await controller.Refresh();

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        refreshTokens.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Refresh_InvalidToken_Returns401_AndClearsCookie()
    {
        var refreshTokens = new Mock<IRefreshTokenService>();
        refreshTokens
            .Setup(s => s.ValidateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RefreshTokenValidationResult.NotFound());

        var controller = BuildController(refreshTokens.Object);
        AttachCookie(controller, "refreshToken", "bogus");

        var result = await controller.Refresh();

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        // Cookie delete is applied via Response.Cookies.Delete — verify Set-Cookie header
        var setCookie = controller.ControllerContext.HttpContext.Response.Headers.SetCookie.ToString();
        Assert.Contains("refreshToken=", setCookie);
        Assert.Contains("expires=", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    private static AuthController BuildController(IRefreshTokenService refreshTokens)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"authcontroller-tests-{Guid.NewGuid()}")
            .Options);

        var passwordHasher = new Mock<IPasswordHasher>().Object;
        var tokenService = new Mock<ITokenService>().Object;
        var settingsService = new Mock<ISettingsService>().Object;
        var userPrefsService = new Mock<IUserPreferencesService>().Object;
        var totpService = new Mock<ITotpService>().Object;
        var trustedDevices = new Mock<ITrustedDeviceService>().Object;
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Development");
        var logger = new Mock<ILogger<AuthController>>().Object;

        var controller = new AuthController(
            db, passwordHasher, tokenService, refreshTokens,
            settingsService, userPrefsService, totpService, trustedDevices, env.Object, logger);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        return controller;
    }

    private static void AttachCookie(ControllerBase controller, string name, string value)
    {
        var http = controller.ControllerContext.HttpContext;
        http.Request.Headers.Cookie = $"{name}={value}";
    }
}
