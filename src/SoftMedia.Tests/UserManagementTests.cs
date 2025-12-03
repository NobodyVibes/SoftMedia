using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SoftMedia.Server.Services;

namespace SoftMedia.Tests;

public class UserManagementTests
{
    private AppDbContext GetInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private UsersController GetUsersController(AppDbContext context, User currentUser)
    {
        var mockPasswordHasher = new Mock<IPasswordHasher>();
        mockPasswordHasher.Setup(x => x.HashPassword(It.IsAny<string>())).Returns("hashed_password");

        var controller = new UsersController(context, mockPasswordHasher.Object);
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, currentUser.Id.ToString()),
            new Claim("sub", currentUser.Id.ToString()), // Added sub claim
            new Claim(ClaimTypes.Name, currentUser.Username),
            new Claim(ClaimTypes.Role, currentUser.Role.ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new ClaimsPrincipal(identity);
        
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };

        return controller;
    }

    [Fact]
    public async Task GetUsers_ReturnsAllUsers()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        var admin = new User { Username = "admin", Role = UserRole.Admin };
        var user1 = new User { Username = "user1", Role = UserRole.User };
        context.Users.AddRange(admin, user1);
        await context.SaveChangesAsync();

        var controller = GetUsersController(context, admin);

        // Act
        var result = await controller.GetUsers();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var users = Assert.IsAssignableFrom<IEnumerable<UserDto>>(okResult.Value);
        Assert.Equal(2, users.Count());
    }

    [Fact]
    public async Task BanUser_BansUser()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        var admin = new User { Username = "admin", Role = UserRole.Admin };
        var user1 = new User { Username = "user1", Role = UserRole.User };
        context.Users.AddRange(admin, user1);
        await context.SaveChangesAsync();

        var controller = GetUsersController(context, admin);

        // Act
        var result = await controller.BanUser(user1.Id, new BanUserRequest(true));

        // Assert
        Assert.IsType<OkResult>(result);
        var bannedUser = await context.Users.FindAsync(user1.Id);
        Assert.True(bannedUser.IsBanned);
    }

    [Fact]
    public async Task BanUser_CannotBanSelf()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        var admin = new User { Username = "admin", Role = UserRole.Admin };
        context.Users.Add(admin);
        await context.SaveChangesAsync();

        var controller = GetUsersController(context, admin);

        // Act
        var result = await controller.BanUser(admin.Id, new BanUserRequest(true));

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Cannot ban yourself.", badRequest.Value);
    }

    [Fact]
    public async Task UpdateUserRole_UpdatesRole()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        var admin = new User { Username = "admin", Role = UserRole.Admin };
        var user1 = new User { Username = "user1", Role = UserRole.User };
        context.Users.AddRange(admin, user1);
        await context.SaveChangesAsync();

        var controller = GetUsersController(context, admin);

        // Act
        var result = await controller.UpdateUserRole(user1.Id, new UpdateUserRoleRequest("Admin"));

        // Assert
        Assert.IsType<OkResult>(result);
        var updatedUser = await context.Users.FindAsync(user1.Id);
        Assert.Equal(UserRole.Admin, updatedUser.Role);
    }

    [Fact]
    public async Task DeleteUser_DeletesUser()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        var admin = new User { Username = "admin", Role = UserRole.Admin };
        var user1 = new User { Username = "user1", Role = UserRole.User };
        context.Users.AddRange(admin, user1);
        await context.SaveChangesAsync();

        var controller = GetUsersController(context, admin);

        // Act
        var result = await controller.DeleteUser(user1.Id);

        // Assert
        Assert.IsType<OkResult>(result);
        var deletedUser = await context.Users.FindAsync(user1.Id);
        Assert.Null(deletedUser);
    }
    [Fact]
    public async Task ApproveUser_ApprovesUser()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        var admin = new User { Username = "admin", Role = UserRole.Admin };
        var user1 = new User { Username = "user1", Role = UserRole.User, IsApproved = false };
        context.Users.AddRange(admin, user1);
        await context.SaveChangesAsync();

        var controller = GetUsersController(context, admin);

        // Act
        var result = await controller.ApproveUser(user1.Id, new ApproveUserRequest(true));

        // Assert
        Assert.IsType<OkResult>(result);
        var approvedUser = await context.Users.FindAsync(user1.Id);
        Assert.True(approvedUser.IsApproved);
    }

    [Fact]
    public async Task DenyUser_DeniesUser()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        var admin = new User { Username = "admin", Role = UserRole.Admin };
        var user1 = new User { Username = "user1", Role = UserRole.User, IsApproved = false };
        context.Users.AddRange(admin, user1);
        await context.SaveChangesAsync();

        var controller = GetUsersController(context, admin);

        // Act
        var result = await controller.DenyUser(user1.Id);

        // Assert
        Assert.IsType<OkResult>(result);
        var deniedUser = await context.Users.FindAsync(user1.Id);
        Assert.True(deniedUser.IsRejected);
        Assert.False(deniedUser.IsApproved);
    }

    [Fact]
    public async Task CreateUser_CreatesUser()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        var admin = new User { Username = "admin", Role = UserRole.Admin };
        context.Users.Add(admin);
        await context.SaveChangesAsync();

        var controller = GetUsersController(context, admin);
        var request = new CreateUserRequest("newuser", "password123", "User");

        // Act
        var result = await controller.CreateUser(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var userDto = Assert.IsType<UserDto>(okResult.Value);
        Assert.Equal("newuser", userDto.Username);
        Assert.True(userDto.IsApproved);
        
        var dbUser = await context.Users.FirstOrDefaultAsync(u => u.Username == "newuser");
        Assert.NotNull(dbUser);
    }

    [Fact]
    public async Task UpdateUserRatings_UpdatesRatings()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        var admin = new User { Username = "admin", Role = UserRole.Admin };
        var user = new User { Username = "user1", Role = UserRole.User };
        context.Users.AddRange(admin, user);
        await context.SaveChangesAsync();

        var controller = GetUsersController(context, admin);
        var ratings = new Dictionary<string, string> { { "Movie", "R" }, { "Game", "M" } };
        var request = new UpdateUserRatingsRequest(ratings);

        // Act
        var result = await controller.UpdateUserRatings(user.Id, request);

        // Assert
        Assert.IsType<OkResult>(result);
        var dbUser = await context.Users.FindAsync(user.Id);
        Assert.Contains("R", dbUser.ContentRatings);
        Assert.Contains("M", dbUser.ContentRatings);
    }
}
