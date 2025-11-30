using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class LibrariesController : ControllerBase
{
    private readonly AppDbContext _context;

    public LibrariesController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Library>>> GetLibraries()
    {
        return await _context.Libraries.ToListAsync();
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

    [HttpGet("{id}/items")]
    public async Task<ActionResult<PagedResult<MediaItem>>> GetLibraryItems(
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

        return new PagedResult<MediaItem>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }
}
