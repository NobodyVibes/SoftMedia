using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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
        var controller = new UsersController(context);
        var claims = new List<Claim>
        {
            new Claim("sub", currentUser.Id.ToString()),
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
}
