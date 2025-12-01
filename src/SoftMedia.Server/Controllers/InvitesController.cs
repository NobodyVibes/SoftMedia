using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using System.Security.Cryptography;

namespace SoftMedia.Server.Controllers;

[Route("api/v1/[controller]")]
[ApiController]
[Authorize(Roles = "Admin")]
public class InvitesController : ControllerBase
{
    private readonly AppDbContext _context;

    public InvitesController(AppDbContext context)
    {
        _context = context;
    }

    [HttpPost]
    public async Task<ActionResult<InviteDto>> CreateInvite(CreateInviteRequest request)
    {
        // Get current user ID from claims
        var currentUserId = User.FindFirst("sub")?.Value 
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

        if (currentUserId == null || !Guid.TryParse(currentUserId, out var userId))
        {
            return Unauthorized();
        }

        var invite = new Invite
        {
            Code = GenerateInviteCode(),
            CreatedById = userId,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = request.ExpiresInHours.HasValue 
                ? DateTime.UtcNow.AddHours(request.ExpiresInHours.Value) 
                : null
        };

        _context.Invites.Add(invite);
        await _context.SaveChangesAsync();

        var dto = new InviteDto(
            invite.Code,
            invite.CreatedAt,
            invite.ExpiresAt,
            null,
            null
        );

        return Ok(dto);
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<InviteDto>>> GetInvites()
    {
        var invites = await _context.Invites
            .Include(i => i.UsedBy)
            .Select(i => new InviteDto(
                i.Code,
                i.CreatedAt,
                i.ExpiresAt,
                i.UsedAt,
                i.UsedBy != null ? i.UsedBy.Username : null
            ))
            .ToListAsync();

        return Ok(invites);
    }

    [HttpDelete("{code}")]
    public async Task<IActionResult> RevokeInvite(string code)
    {
        var invite = await _context.Invites.FirstOrDefaultAsync(i => i.Code == code);
        if (invite == null)
        {
            return NotFound("Invite not found.");
        }

        invite.IsRevoked = true;
        await _context.SaveChangesAsync();

        return Ok();
    }

    private static string GenerateInviteCode()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var code = new char[12];
        
        for (int i = 0; i < code.Length; i++)
        {
            code[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        }
        
        return new string(code);
    }
}
