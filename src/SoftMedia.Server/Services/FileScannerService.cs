using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Metadata;

namespace SoftMedia.Server.Services;

public interface IFileScannerService
{
    Task ScanLibraryAsync(Guid libraryId);
    Task ScanAllLibrariesAsync();
}

public class FileScannerService : IFileScannerService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FileScannerService> _logger;
    private readonly IFileSystem _fileSystem;
    private readonly IMetadataRouter _metadataRouter;
    private readonly string[] _videoExtensions = { ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".webm" };
    private readonly string[] _audioExtensions = { ".mp3", ".flac", ".aac", ".wav", ".ogg", ".m4a" };

    public FileScannerService(IServiceScopeFactory scopeFactory, ILogger<FileScannerService> logger, IFileSystem fileSystem, IMetadataRouter metadataRouter)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _fileSystem = fileSystem;
        _metadataRouter = metadataRouter;
    }

    public async Task ScanAllLibrariesAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var libraries = await context.Libraries.ToListAsync();

        foreach (var library in libraries)
        {
            await ScanLibraryAsync(library.Id);
        }
    }

    public async Task ScanLibraryAsync(Guid libraryId)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var library = await context.Libraries.FindAsync(libraryId);

        if (library == null)
        {
            _logger.LogWarning($"Library with ID {libraryId} not found.");
            return;
        }

        _logger.LogInformation($"Scanning library: {library.Name}");

        foreach (var path in library.Paths)
        {
            if (!_fileSystem.DirectoryExists(path))
            {
                _logger.LogWarning($"Directory not found: {path}");
                continue;
            }

            var files = _fileSystem.GetFiles(path, "*.*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                if (!IsMediaFile(file, library.Type)) continue;

                if (await context.MediaItems.AnyAsync(m => m.Path == file && m.LibraryId == libraryId))
                {
                    continue;
                }

                var title = Path.GetFileNameWithoutExtension(file);
                var metadataJson = await _metadataRouter.FetchMetadataAsync(title, file, library.Type);

                var mediaItem = new MediaItem
                {
                    Id = Guid.NewGuid(),
                    LibraryId = libraryId,
                    Title = title,
                    Path = file,
                    Size = _fileSystem.GetFileLength(file),
                    DateAdded = DateTime.UtcNow,
                    DateModified = _fileSystem.GetLastWriteTimeUtc(file),
                    Container = _fileSystem.GetExtension(file).TrimStart('.').ToUpper(),
                    MetadataJson = metadataJson
                };

                context.MediaItems.Add(mediaItem);
                _logger.LogInformation($"Added media: {mediaItem.Title}");
            }
        }

        await context.SaveChangesAsync();
        _logger.LogInformation($"Finished scanning library: {library.Name}");
    }

    private bool IsMediaFile(string path, LibraryType type)
    {
        var ext = _fileSystem.GetExtension(path).ToLower();
        return type switch
        {
            LibraryType.Movie or LibraryType.TV => _videoExtensions.Contains(ext),
            LibraryType.Music => _audioExtensions.Contains(ext),
            _ => false
        };
    }
}
