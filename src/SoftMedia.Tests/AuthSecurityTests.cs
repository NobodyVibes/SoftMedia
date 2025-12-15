using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services;
using Xunit;

namespace SoftMedia.Tests;

public class AuthSecurityTests
{
    private AppDbContext GetInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private void SetupControllerContext(AuthController controller)
    {
        var httpContext = new DefaultHttpContext();
        // Setup response features to allow cookie operations
        httpContext.Response.Body = new MemoryStream();
        controller.ControllerContext = new ControllerContext()
        {
            HttpContext = httpContext
        };
    }

    [Fact]
    public async Task Signup_WhenAllowSignupIsFalse_ShouldReturnForbidden()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        // Seed an existing user so that the "First User" check fails
        context.Users.Add(new User { Username = "admin", Role = UserRole.Admin });
        await context.SaveChangesAsync();

        var mockPasswordHasher = new Mock<IPasswordHasher>();
        mockPasswordHasher.Setup(x => x.HashPassword(It.IsAny<string>())).Returns("hashed_password");
        var mockTokenService = new Mock<ITokenService>();
        
        // Mock SettingsService to return false for AllowUserSignup
        var mockSettingsService = new Mock<ISettingsService>();
        mockSettingsService.Setup(x => x.GetSettingAsync("AllowUserSignup", "Disabled")).ReturnsAsync("Disabled");

        var controller = new AuthController(context, mockPasswordHasher.Object, mockTokenService.Object, mockSettingsService.Object);
        SetupControllerContext(controller);
        
        // Act
        var result = await controller.Signup(new SignupRequest("newuser", "password", null, "New", "User"));

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Login_WhenUserIsNotApproved_ShouldReturnUnauthorized()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        var user = new User 
        { 
            Username = "pending_user", 
            PasswordHash = "hash",
            IsApproved = false
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var mockPasswordHasher = new Mock<IPasswordHasher>();
        mockPasswordHasher.Setup(x => x.VerifyPassword("password", "hash")).Returns(true);
        var mockTokenService = new Mock<ITokenService>();
        var mockSettingsService = new Mock<ISettingsService>();

        var controller = new AuthController(context, mockPasswordHasher.Object, mockTokenService.Object, mockSettingsService.Object);
        SetupControllerContext(controller);

        // Act
        var result = await controller.Login(new LoginRequest("pending_user", "password"));

        // Assert
        var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal("Account pending approval.", unauthorizedResult.Value);
    }

    [Fact]
    public async Task Signup_FirstUser_ShouldBeAdminAndApproved()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        // Empty DB
        
        var mockPasswordHasher = new Mock<IPasswordHasher>();
        mockPasswordHasher.Setup(x => x.HashPassword(It.IsAny<string>())).Returns("hashed_password");
        var mockTokenService = new Mock<ITokenService>();
        // Setup GenerateRefreshToken to avoid NullReferenceException
        mockTokenService.Setup(x => x.GenerateRefreshToken()).Returns("refresh_token");
        mockTokenService.Setup(x => x.GenerateAccessToken(It.IsAny<User>())).Returns("access_token");

        var mockSettingsService = new Mock<ISettingsService>();
        // Even if signup is disabled, first user should be allowed
        mockSettingsService.Setup(x => x.GetSettingAsync("AllowUserSignup", "Disabled")).ReturnsAsync("Disabled");

        var controller = new AuthController(context, mockPasswordHasher.Object, mockTokenService.Object, mockSettingsService.Object);
        SetupControllerContext(controller);

        // Act
        var result = await controller.Signup(new SignupRequest("admin", "password", null, "Admin", "User"));

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<AuthResponse>(okResult.Value);
        
        Assert.Equal(UserRole.Admin, response.User.Role);
        
        // Verify in DB
        var user = await context.Users.FirstAsync();
        Assert.True(user.IsApproved);
        Assert.Equal(UserRole.Admin, user.Role);
    }
}
