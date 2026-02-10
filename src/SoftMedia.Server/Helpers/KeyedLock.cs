using System.Collections.Concurrent;

namespace SoftMedia.Server.Helpers;

/// <summary>
/// Provides a mechanism to lock based on a key (e.g., MediaId).
/// Prevents concurrent execution of code blocks for the same key.
/// </summary>
public class KeyedLock
{
    private readonly ConcurrentDictionary<object, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> LockAsync(object key)
    {
        var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        return new Releaser(key, semaphore, _locks);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly object _key;
        private readonly SemaphoreSlim _semaphore;
        private readonly ConcurrentDictionary<object, SemaphoreSlim> _locks;
        private bool _disposed;

        public Releaser(object key, SemaphoreSlim semaphore, ConcurrentDictionary<object, SemaphoreSlim> locks)
        {
            _key = key;
            _semaphore = semaphore;
            _locks = locks;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _semaphore.Release();
            
            // Cleanup: If no one is waiting, try to remove the lock to free memory.
            // Note: This is a bit tricky in high concurrency, but GetOrAdd handles re-creation safely.
            // For rigorous correctness, we might leave them or use a ref-counting mechanism, 
            // but for this use case (Transient background tasks), basic removal is often sufficient 
            // or we can rely on specific eviction policies. 
            // For simplicity and safety in this specific implementation, we will keep the lock in the dictionary
            // to avoid the complex race of removing a lock that another thread is just about to acquire.
            // If memory becomes an issue, a more complex "RefCountingKeyedLock" would be needed.
        }
    }
}
