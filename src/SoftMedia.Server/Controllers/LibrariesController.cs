using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;

using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services;

namespace SoftMedia.Server.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class LibrariesController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IFileScannerService _fileScanner;

    public LibrariesController(AppDbContext context, IFileScannerService fileScanner)
    {
        _context = context;
        _fileScanner = fileScanner;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Library>>> GetLibraries()
    {
        return await _context.Libraries.OrderBy(l => l.Order).ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Library>> GetLibrary(Guid id)
    {
        var library = await _context.Libraries.FindAsync(id);

        if (library == null)
        {
            return NotFound();
        }

        return library;
    }

    [HttpPost]
    public async Task<ActionResult<Library>> CreateLibrary(CreateLibraryRequest request)
    {
        // Validate paths exist
        foreach (var path in request.Paths)
        {
            if (!Directory.Exists(path))
            {
                return BadRequest($"Directory does not exist: {path}");
            }
        }

        // Validate duplicate paths across libraries
        var allLibraries = await _context.Libraries.ToListAsync();
        foreach (var lib in allLibraries)
        {
            foreach (var path in request.Paths)
            {
                if (lib.Paths.Contains(path))
                {
                    return BadRequest($"Path '{path}' is already used by library '{lib.Name}'.");
                }
            }
        }

        var library = new Library
        {
            Name = request.Name,
            Type = request.Type,
            Paths = request.Paths,
            Order = await _context.Libraries.CountAsync() // Add to end
        };

        _context.Libraries.Add(library);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetLibrary), new { id = library.Id }, library);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateLibrary(Guid id, UpdateLibraryRequest request)
    {
        var library = await _context.Libraries.FindAsync(id);

        if (library == null)
        {
            return NotFound();
        }

        // Validate paths exist
        foreach (var path in request.Paths)
        {
            if (!Directory.Exists(path))
            {
                return BadRequest($"Directory does not exist: {path}");
            }
        }

        // Validate duplicate paths across libraries (excluding current)
        var otherLibraries = await _context.Libraries.Where(l => l.Id != id).ToListAsync();
        foreach (var lib in otherLibraries)
        {
            foreach (var path in request.Paths)
            {
                if (lib.Paths.Contains(path))
                {
                    return BadRequest($"Path '{path}' is already used by library '{lib.Name}'.");
                }
            }
        }

        library.Name = request.Name;
        library.Type = request.Type;
        library.Paths = request.Paths;

        await _context.SaveChangesAsync();

        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteLibrary(Guid id)
    {
        var library = await _context.Libraries.FindAsync(id);
        if (library == null)
        {
            return NotFound();
        }

        _context.Libraries.Remove(library);
        await _context.SaveChangesAsync();

        return NoContent();
    }

    [HttpPut("reorder")]
    public async Task<IActionResult> ReorderLibraries([FromBody] List<Guid> orderedIds)
    {
        var libraries = await _context.Libraries.ToListAsync();
        
        foreach (var library in libraries)
        {
            var index = orderedIds.IndexOf(library.Id);
            if (index != -1)
            {
                library.Order = index;
            }
        }

        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id}/scan")]
    public async Task<IActionResult> ScanLibrary(Guid id)
    {
        var library = await _context.Libraries.FindAsync(id);
        if (library == null)
        {
            return NotFound();
        }

        // Run in background? Or await? 
        // For now, await to report errors, but ideally should be background job.
        // Given it's a personal server, awaiting is okay for immediate feedback, 
        // but might timeout for large libraries.
        // Better: Fire and forget, or return Accepted.
        // The requirement says "Trigger immediate library scan".
        // I'll use fire-and-forget for the HTTP request but log it.
        // Actually, FileScannerService.ScanLibraryAsync is async.
        // If I await it, the UI hangs.
        // I will run it in a background task.
        _ = Task.Run(async () => 
        {
            try 
            {
                await _fileScanner.ScanLibraryAsync(id);
            }
            catch (Exception ex)
            {
                // Log error (logger not injected in controller, but FileScanner logs internally)
                Console.WriteLine($"Error scanning library {id}: {ex.Message}");
            }
        });

        return Accepted(new { message = "Scan started" });
    }

    [HttpGet("{id}/items")]
    public async Task<ActionResult<PagedResult<MediaItemDto>>> GetLibraryItems(
        Guid id, 
        [FromQuery] int page = 1, 
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = null)
    {
        if (!await _context.Libraries.AnyAsync(l => l.Id == id))
        {
            return NotFound("Library not found.");
        }

        var query = _context.MediaItems.Where(m => m.LibraryId == id);

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(m => m.Title.ToLower().Contains(search.ToLower()));
        }

        // Sorting
        query = sortBy?.ToLower() switch
        {
            "title" => query.OrderBy(m => m.Title),
            "dateadded" => query.OrderByDescending(m => m.DateAdded),
            _ => query.OrderBy(m => m.Title)
        };

        var totalCount = await query.CountAsync();
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var dtos = items.Select(i => MediaItemDto.FromMediaItem(i, "/api/v1/image/proxy")).ToList();

        return new PagedResult<MediaItemDto>
        {
            Items = dtos,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }
}
