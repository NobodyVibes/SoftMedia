using System.Text.Json;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Security;

/// T6.6 — media-route URLs carry `?token=`/`?access_token=` JWTs, and ASP.NET's
/// `Microsoft.AspNetCore.Hosting.Diagnostics` category logs the FULL request URL
/// ("Request starting …") at Information. The app itself never logs query strings,
/// so the one leak path is an operator raising log levels for debugging. These
/// tests pin the explicit category override that keeps request-URL logging
/// suppressed even when `Default` or `Microsoft.AspNetCore` is raised — the
/// more-specific category always wins in the logging config chain.
public class LoggingTokenScrubConfigTests
{
    private static string RepoServerDir()
    {
        // Walk up from the test bin directory to the repo's server project.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "SoftMedia.Server")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "SoftMedia.Server");
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void HostingDiagnostics_IsPinnedToWarningOrHigher(string fileName)
    {
        var path = Path.Combine(RepoServerDir(), fileName);
        Assert.True(File.Exists(path), $"Missing {fileName}");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var logLevel = doc.RootElement.GetProperty("Logging").GetProperty("LogLevel");

        Assert.True(
            logLevel.TryGetProperty("Microsoft.AspNetCore.Hosting.Diagnostics", out var pin),
            $"{fileName} must pin 'Microsoft.AspNetCore.Hosting.Diagnostics' — its Information-level " +
            "'Request starting' lines include full media-route URLs with ?token= JWTs (T6.6).");

        Assert.Contains(pin.GetString(), new[] { "Warning", "Error", "Critical", "None" });
    }
}
