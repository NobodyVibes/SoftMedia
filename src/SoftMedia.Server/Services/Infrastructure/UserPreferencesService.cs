using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Infrastructure;

/// <summary>
/// Service for managing per-user preferences stored on the server.
/// </summary>
public class UserPreferencesService : IUserPreferencesService
{
    private readonly AppDbContext _context;
    private readonly ILogger<UserPreferencesService> _logger;

    /// <summary>
    /// Default preference values for new users.
    /// </summary>
    private static readonly Dictionary<string, string> DefaultPreferences = new()
    {
        { "Language", "en-US" },
        { "SubtitleLanguage", "en" },
        { "AudioLanguage", "en" },
        { "AutoSelectSubtitle", "true" }
    };

    public UserPreferencesService(AppDbContext context, ILogger<UserPreferencesService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<Dictionary<string, string>> GetPreferencesAsync(Guid userId)
    {
        var preferences = await _context.UserPreferences
            .Where(p => p.UserId == userId)
            .ToDictionaryAsync(p => p.Key, p => p.Value);

        // Merge with defaults for any missing keys
        foreach (var defaultPref in DefaultPreferences)
        {
            if (!preferences.ContainsKey(defaultPref.Key))
            {
                preferences[defaultPref.Key] = defaultPref.Value;
            }
        }

        return preferences;
    }

    public async Task<string> GetPreferenceAsync(Guid userId, string key, string defaultValue)
    {
        var preference = await _context.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Key == key);

        return preference?.Value ?? defaultValue;
    }

    public async Task SetPreferenceAsync(Guid userId, string key, string value)
    {
        var existing = await _context.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Key == key);

        if (existing != null)
        {
            existing.Value = value;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _context.UserPreferences.Add(new UserPreference
            {
                UserId = userId,
                Key = key,
                Value = value,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync();
    }

    public async Task SetPreferencesAsync(Guid userId, Dictionary<string, string> preferences)
    {
        foreach (var (key, value) in preferences)
        {
            var existing = await _context.UserPreferences
                .FirstOrDefaultAsync(p => p.UserId == userId && p.Key == key);

            if (existing != null)
            {
                existing.Value = value;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                _context.UserPreferences.Add(new UserPreference
                {
                    UserId = userId,
                    Key = key,
                    Value = value,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }

        await _context.SaveChangesAsync();
        _logger.LogDebug("Updated {Count} preferences for user {UserId}", preferences.Count, userId);
    }

    public async Task InitializeDefaultsAsync(Guid userId)
    {
        // Only add defaults that don't already exist
        var existingKeys = await _context.UserPreferences
            .Where(p => p.UserId == userId)
            .Select(p => p.Key)
            .ToListAsync();

        var toAdd = DefaultPreferences
            .Where(d => !existingKeys.Contains(d.Key))
            .Select(d => new UserPreference
            {
                UserId = userId,
                Key = d.Key,
                Value = d.Value,
                UpdatedAt = DateTime.UtcNow
            });

        _context.UserPreferences.AddRange(toAdd);
        await _context.SaveChangesAsync();
        
        _logger.LogDebug("Initialized default preferences for user {UserId}", userId);
    }
}
