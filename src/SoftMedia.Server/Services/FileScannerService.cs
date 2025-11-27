using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

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
    private readonly string[] _videoExtensions = { ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".webm" };
    private readonly string[] _audioExtensions = { ".mp3", ".flac", ".aac", ".wav", ".ogg", ".m4a" };

    public FileScannerService(IServiceScopeFactory scopeFactory, ILogger<FileScannerService> logger, IFileSystem fileSystem)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _fileSystem = fileSystem;
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

            var files = _fileSystem.GetFiles(path, "*.*", SearchOption.AllDirectories)
                .Where(f => IsMediaFile(f, library.Type));

            foreach (var file in files)
            {
                if (!await context.MediaItems.AnyAsync(m => m.Path == file))
                {
                    var mediaItem = new MediaItem
                    {
                        LibraryId = library.Id,
                        Title = _fileSystem.GetFileNameWithoutExtension(file),
                        Path = file,
                        Size = _fileSystem.GetFileLength(file),
                        DateAdded = DateTime.UtcNow,
                        DateModified = _fileSystem.GetLastWriteTimeUtc(file),
                        Container = _fileSystem.GetExtension(file).TrimStart('.').ToUpper()
                    };

                    context.MediaItems.Add(mediaItem);
                    _logger.LogInformation($"Added media: {mediaItem.Title}");
                }
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
