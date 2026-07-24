using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Scanning;

using SoftMedia.Server.Services.Metadata;

namespace SoftMedia.Server.Tests.Services.Scanning;

public class TestMediaScanner : BaseMediaScanner
{
    public override LibraryType SupportedType => LibraryType.Movie; // Arbitrary for test
    public override string[] SupportedExtensions => new[] { "mkv", "mp4" };
    public override string DisplayName => "TestScanner";

    // Virtual Filesystem: DirectoryPath -> List of FilePaths
    public Dictionary<string, List<string>> VirtualFileSystem { get; set; } = new();
    
    // Track calls to ProcessFileAsync
    public List<string> ProcessedFiles { get; } = new();
    public int ProcessFileCallCount => ProcessedFiles.Count;

    // Expose protected field for testing
    public bool IsStrictEnrichment => _strictEnrichment;

    // Simulate work delay to test concurrency
    public int SimulateWorkDelayMs { get; set; } = 0;

    // Files whose processing should throw (error-count testing)
    public Func<string, bool>? ThrowOnFile { get; set; }

    // Roots reported as unreachable; all other virtual roots exist.
    public HashSet<string> MissingRoots { get; } = new(StringComparer.OrdinalIgnoreCase);

    protected override bool RootExists(string path) => !MissingRoots.Contains(path);

    // Directories whose file listing "fails" (simulated permission error). Mirrors the
    // real enumerator: the failure is recorded in _unreadableDirs during discovery so
    // the subtree is shielded from orphan cleanup.
    public HashSet<string> UnreadableDirs { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Optional per-file (size, mtime) metadata for SR-WI-012 rebind tests. Files without
    // an entry report size 0 (excluded from the size+mtime rebind pass by design) and a
    // fixed mtime so discovery results are deterministic.
    public Dictionary<string, (long Size, DateTime Mtime)> FileMetadata { get; } = new(StringComparer.OrdinalIgnoreCase);
    private static readonly DateTime DefaultMtime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public TestMediaScanner(
        IServiceScopeFactory scopeFactory,
        ILogger<TestMediaScanner> logger,
        IMediaNotificationService notificationService,
        IMetadataQueue metadataQueue)
        : base(scopeFactory, logger, notificationService, metadataQueue)
    {
    }

    protected override IEnumerable<string> EnumerateDirectories(List<string> libraryPaths)
    {
        // For testing, we just return all keys in our virtual FS
        // In reality, this would filter by libraryPaths, but for unit tests we assume the FS matches the library
        return VirtualFileSystem.Keys;
    }

    protected override IEnumerable<FileDiscoveryResult> EnumerateFilesCurrentDir(string dirPath)
    {
        if (UnreadableDirs.Contains(dirPath))
        {
            _unreadableDirs.Add(dirPath);
            return Enumerable.Empty<FileDiscoveryResult>();
        }
        if (VirtualFileSystem.TryGetValue(dirPath, out var files))
        {
            return files.Select(f => FileMetadata.TryGetValue(f, out var md)
                ? new FileDiscoveryResult(f, md.Size, md.Mtime)
                : new FileDiscoveryResult(f, 0, DefaultMtime));
        }
        return Enumerable.Empty<FileDiscoveryResult>();
    }

    protected override async Task<ScanOperationResult> ProcessFileAsync(
        AppDbContext context,
        FileDiscoveryResult file,
        MediaItem? existing,
        Library library,
        CancellationToken cancellationToken)
    {
        var filePath = file.Path;
        if (ThrowOnFile?.Invoke(filePath) == true)
        {
            throw new InvalidOperationException($"Simulated failure for {filePath}");
        }
        if (SimulateWorkDelayMs > 0)
        {
            await Task.Delay(SimulateWorkDelayMs, cancellationToken);
        }

        lock (ProcessedFiles)
        {
            ProcessedFiles.Add(filePath);
        }

        // Verify context is usable
        // We can check if context is disposed by trying to access it? 
        // Or let the framework handle it.
        // We might want to assert that 'context' is different for different directories if we could.
        
        return new ScanOperationResult(ScanResult.New, Guid.Empty, EnqueueMetadata: false);
    }
}
