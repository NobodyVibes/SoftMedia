using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Infrastructure;

public interface INotificationService
{
    Task<List<SystemNotification>> GetActiveAsync();
    Task<SystemNotification> CreateAsync(string type, string title, string message, string severity, string? metadata = null);
    Task DismissAsync(Guid id, string username);
    Task<bool> HasActiveOfTypeAsync(string type);
}

public class NotificationService : INotificationService
{
    private readonly AppDbContext _context;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(AppDbContext context, ILogger<NotificationService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<List<SystemNotification>> GetActiveAsync()
    {
        return await _context.SystemNotifications
            .Where(n => n.DismissedAt == null)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync();
    }

    public async Task<SystemNotification> CreateAsync(string type, string title, string message, string severity, string? metadata = null)
    {
        var notification = new SystemNotification
        {
            Type = type,
            Title = title,
            Message = message,
            Severity = severity,
            Metadata = metadata
        };

        _context.SystemNotifications.Add(notification);
        await _context.SaveChangesAsync();
        
        _logger.LogInformation("Created notification: {Type} - {Title}", type, title);
        return notification;
    }

    public async Task DismissAsync(Guid id, string username)
    {
        var notification = await _context.SystemNotifications.FindAsync(id);
        if (notification != null && notification.DismissedAt == null)
        {
            notification.DismissedAt = DateTime.UtcNow;
            notification.DismissedBy = username;
            await _context.SaveChangesAsync();
            
            _logger.LogInformation("Notification {Id} dismissed by {Username}", id, username);
        }
    }

    public async Task<bool> HasActiveOfTypeAsync(string type)
    {
        return await _context.SystemNotifications
            .AnyAsync(n => n.Type == type && n.DismissedAt == null);
    }
}
