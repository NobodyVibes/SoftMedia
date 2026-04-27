using Xunit;

namespace SoftMedia.Server.Tests.Helpers;

/// xUnit lifecycle wrapper: constructs the factory, clears seed noise, and
/// disposes the SQLite connection on test-class teardown.
public abstract class IntegrationTestBase : IAsyncLifetime
{
    protected SoftMediaWebApplicationFactory Factory { get; private set; } = null!;

    public virtual async Task InitializeAsync()
    {
        Factory = new SoftMediaWebApplicationFactory();
        // Touch the service provider to force host construction (and DbInitializer).
        _ = Factory.Services;
        await Factory.ResetSeedNoiseAsync();
    }

    public virtual Task DisposeAsync()
    {
        Factory.Dispose();
        return Task.CompletedTask;
    }
}
