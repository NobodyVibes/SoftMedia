using Xunit;

namespace SoftMedia.Server.Tests.Helpers;

/// Serialises all integration tests into a single, non-parallel collection. Each integration test
/// class spins up its own host with the full set of background hosted services over a private
/// in-memory SQLite connection. Running many such hosts concurrently let the background workers race
/// the per-class seed reset on that shared connection, surfacing intermittent "database is locked".
/// Disabling parallelisation for this collection means only one integration host runs at a time
/// (matching the behaviour seen when a class runs in isolation); unit-test collections still
/// parallelise. Applied via the base class so every derived integration test inherits it.
[CollectionDefinition("Integration", DisableParallelization = true)]
public class IntegrationCollection { }

/// xUnit lifecycle wrapper: constructs the factory, clears seed noise, and
/// disposes the SQLite connection on test-class teardown.
[Collection("Integration")]
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
