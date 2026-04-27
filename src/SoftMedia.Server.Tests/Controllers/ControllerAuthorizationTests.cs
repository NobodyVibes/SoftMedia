using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.Controllers;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

/// Reflection-based CI guard: every public <see cref="ControllerBase"/>
/// subclass in the Server assembly must either carry a class-level
/// <see cref="AuthorizeAttribute"/> OR be explicitly <see cref="AllowAnonymousAttribute"/>-decorated
/// (e.g. <see cref="AuthController"/> whose login/signup actions are
/// unauthenticated by design). Catches the next new controller that
/// ships without an explicit auth choice.
public class ControllerAuthorizationTests
{
    private static readonly Assembly ServerAssembly = typeof(AuthController).Assembly;

    public static IEnumerable<object[]> AllControllers => ServerAssembly
        .GetTypes()
        .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
        .Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(AllControllers))]
    public void Controller_HasExplicitAuthDecision(Type controllerType)
    {
        var hasAuthorize = controllerType.GetCustomAttribute<AuthorizeAttribute>(inherit: true) is not null;
        var hasAllowAnon = controllerType.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) is not null;

        Assert.True(hasAuthorize || hasAllowAnon,
            $"{controllerType.Name} must carry [Authorize] (optionally with Roles) or " +
            $"[AllowAnonymous] at the class level. Default-open controllers are forbidden — " +
            $"if an endpoint must be public, set [AllowAnonymous] on that specific action " +
            $"under a class-level [Authorize].");
    }

    [Fact]
    public void AudioController_DumpBooks_IsRemoved()
    {
        // Load-bearing regression guard: the unauthenticated catalogue-dump
        // endpoint flagged by the 2026-04-23 audit must never come back.
        var method = typeof(AudioController).GetMethod("DumpBooks", BindingFlags.Public | BindingFlags.Instance);
        Assert.Null(method);
    }
}
