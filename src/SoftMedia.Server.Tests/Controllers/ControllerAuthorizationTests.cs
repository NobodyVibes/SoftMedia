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

    // ---- 2026-04-26 hardening regression guards ---------------------------

    [Fact]
    public void TranscodeController_GetFramePreview_DoesNotAcceptTokenParameter()
    {
        // A1: bespoke `?token=` decode-only check (`JwtSecurityTokenHandler.ReadJwtToken`)
        // was removed because it added no security on top of the class-level [Authorize]
        // and was a copy-paste hazard. Standard JWT bearer middleware (via
        // OnMessageReceived for /api/transcode/*) validates query-string tokens.
        var method = typeof(TranscodeController).GetMethod(
            "GetFramePreview", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        Assert.DoesNotContain(method!.GetParameters(),
            p => p.Name == "token" || p.Name == "access_token");
    }

    [Fact]
    public void Phase1_CriticalCatchBlocks_DoNotLeakExceptionMessage()
    {
        // A5: framework exception messages (FileNotFoundException etc.) include
        // internal filesystem paths. The plan stripped `ex.Message` from the
        // generic 500-returning catch blocks in TranscodeController and
        // AudioStreamController. This guard scans the source so the regression
        // can't slip in via copy-paste.
        var roots = new[]
        {
            FindRepoRelative("src/SoftMedia.Server/Controllers/TranscodeController.cs"),
            FindRepoRelative("src/SoftMedia.Server/Controllers/AudioStreamController.cs"),
        };

        foreach (var path in roots)
        {
            var text = File.ReadAllText(path);
            // Forbid string-interpolating ex.Message into the response body.
            Assert.DoesNotContain("StatusCode(500, $\"", text);
            Assert.DoesNotContain("StatusCode(500, ex.Message", text);
            Assert.DoesNotContain("error = ex.Message", text);
            Assert.DoesNotContain("NotFound(ex.Message", text);
        }
    }

    [Fact]
    public void Phase4_ImageController_DoesNotSpoofBrowserUserAgent()
    {
        // C2: ImageController must use the named "ImageProxy" HttpClient (which
        // carries SoftMediaUserAgentHandler). Spoofing a browser User-Agent
        // violates SDD §4.3 attribution requirements toward Wikidata,
        // MusicBrainz, Open Library, and TVMaze.
        var controllerPath = FindRepoRelative("src/SoftMedia.Server/Controllers/ImageController.cs");
        var controllerText = File.ReadAllText(controllerPath);
        Assert.DoesNotContain("Mozilla/5.0", controllerText);
        Assert.DoesNotContain("AppleWebKit", controllerText);
        Assert.Contains("CreateClient(\"ImageProxy\")", controllerText);

        // And confirm the named client is registered with the UA handler attached.
        var extPath = FindRepoRelative("src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs");
        var extText = File.ReadAllText(extPath);
        Assert.Contains("AddHttpClient(\"ImageProxy\"", extText);
    }

    [Fact]
    public void Phase1_ProductionAppsettings_DefaultsCorsAndJwtTtlToSafeValues()
    {
        // A3 + A4: the *shipped* appsettings.json must default to a safe state.
        // The dev override lives in appsettings.Development.json.
        var path = FindRepoRelative("src/SoftMedia.Server/appsettings.json");
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;

        var allowAnyOrigin = root.GetProperty("Cors").GetProperty("AllowAnyOriginForLAN").GetBoolean();
        Assert.False(allowAnyOrigin,
            "appsettings.json must NOT default Cors:AllowAnyOriginForLAN=true. " +
            "Override to true only in appsettings.Development.json for the Vite dev proxy.");

        var ttl = int.Parse(root.GetProperty("JwtSettings").GetProperty("ExpiryMinutes").GetString()!);
        Assert.True(ttl <= 15,
            $"JwtSettings:ExpiryMinutes must be <= 15 (was {ttl}). Reverse-proxy access logs " +
            "capture ?access_token= URLs; a long TTL widens the exposure window.");
    }

    // Walks up from the test bin directory to find the repo root, then resolves
    // a path relative to it. Avoids hard-coding bin/Debug/net8.0 levels.
    private static string FindRepoRelative(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "SoftMedia.Server", "SoftMedia.Server.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
