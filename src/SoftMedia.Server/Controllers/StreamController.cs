using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using SoftMedia.Server.Services.Abstractions;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// Controller for serving media streams with HTTP Range Request support.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class StreamController : ControllerBase
{
    private readonly IMediaService _mediaService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StreamController> _logger;

    public StreamController(
        IMediaService mediaService,
        IConfiguration configuration,
        ILogger<StreamController> logger)
    {
        _mediaService = mediaService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Streams the media file with HTTP Range Request support for seeking.
    /// Supports both GET (stream content) and HEAD (probe headers) for vidstack compatibility.
    /// Accepts auth via Bearer token or ?token= query parameter for audio/video elements.
    /// </summary>
    [HttpGet("{id}")]
    [HttpHead("{id}")]
    public async Task<IActionResult> GetStream(Guid id, [FromQuery] string? token = null)
    {
        // Validate authentication - either from header or query string
        if (!User.Identity?.IsAuthenticated ?? true)
        {
            // Try query-string token fallback for audio/video elements
            if (!string.IsNullOrEmpty(token) && ValidateToken(token))
            {
                // Token is valid, proceed
            }
            else
            {
                return Unauthorized();
            }
        }

        try
        {
            var streamInfo = await _mediaService.GetStreamInfoAsync(id);

            if (streamInfo == null)
            {
                return NotFound();
            }

            // Serve the file with Range processing enabled (HTTP 206 Partial Content)
            return PhysicalFile(streamInfo.Path, streamInfo.ContentType, enableRangeProcessing: true);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    /// <summary>
    /// Validate a JWT token from the query string.
    /// </summary>
    private bool ValidateToken(string token)
    {
        try
        {
            var jwtKey = _configuration["Jwt:Key"];
            if (string.IsNullOrEmpty(jwtKey))
            {
                _logger.LogWarning("JWT key not configured");
                return false;
            }

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
            var handler = new JwtSecurityTokenHandler();
            
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _configuration["Jwt:Issuer"],
                ValidAudience = _configuration["Jwt:Audience"],
                IssuerSigningKey = key
            };

            handler.ValidateToken(token, validationParameters, out _);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Token validation failed");
            return false;
        }
    }
}
