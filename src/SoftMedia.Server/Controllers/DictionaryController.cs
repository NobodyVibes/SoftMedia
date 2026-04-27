using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// ER-051: word lookup against a locally-bundled dictionary. No network. When
/// the dictionary dataset is absent the endpoint returns 501 Not Implemented
/// so the client can render a "install the dictionary" state instead of a
/// generic network error.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/dictionary")]
public class DictionaryController : ControllerBase
{
    private readonly IDictionaryService _dictionary;

    public DictionaryController(IDictionaryService dictionary)
    {
        _dictionary = dictionary;
    }

    [HttpGet("{word}")]
    public async Task<ActionResult<DictionaryLookupResponse>> Lookup(string word, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return BadRequest("Word must not be empty.");
        }

        if (!_dictionary.Available)
        {
            // 501 signals "feature understood, data not shipped" — the client
            // uses this to render an explanatory empty state rather than the
            // generic connectivity-failure path.
            return StatusCode(StatusCodes.Status501NotImplemented, new DictionaryLookupResponse
            {
                Word = word,
                Definitions = Array.Empty<string>(),
                Available = false,
            });
        }

        var defs = await _dictionary.LookupAsync(word, cancellationToken);
        return Ok(new DictionaryLookupResponse
        {
            Word = word,
            Definitions = defs ?? Array.Empty<string>(),
            Available = true,
        });
    }
}

public class DictionaryLookupResponse
{
    public string Word { get; set; } = string.Empty;
    public IReadOnlyList<string> Definitions { get; set; } = Array.Empty<string>();
    /// <summary>False when the dictionary dataset isn't installed. Paired with a 501 status.</summary>
    public bool Available { get; set; }
}
