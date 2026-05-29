using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// Liveness endpoint. Intentionally anonymous and dependency-free so external
/// monitors (and the restore-staging workflow in P1-WI-001) have a stable
/// signal that the process is up without authenticating.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[AllowAnonymous]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok", utc = DateTime.UtcNow });
}
