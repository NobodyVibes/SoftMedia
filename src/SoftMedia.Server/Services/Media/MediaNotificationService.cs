using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using SoftMedia.Server.Hubs;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Pushes real-time notifications to connected SignalR clients.
/// Uses batching (500ms) to avoid flooding clients during rapid scans.
/// </summary>
public class MediaNotificationService : IMediaNotificationService, IDisposable
{
    private readonly IHubContext<MediaHub> _hubContext;
    private readonly ILogger<MediaNotificationService> _logger;
    private readonly Channel<Notification> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _processTask;

    public MediaNotificationService(IHubContext<MediaHub> hubContext, ILogger<MediaNotificationService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
        _channel = Channel.CreateBounded<Notification>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _cts = new CancellationTokenSource();
        _processTask = Task.Run(ProcessLoopAsync);
    }

    public void NotifyItemAdded(Guid libraryId, Guid itemId, string itemType, string title)
    {
        _channel.Writer.TryWrite(new Notification
        {
            Type = NotificationType.ItemAdded,
            LibraryId = libraryId,
            MediaId = itemId,
            ItemType = itemType,
            Title = title
        });
    }

    public void NotifyItemUpdated(Guid mediaId)
    {
        _channel.Writer.TryWrite(new Notification
        {
            Type = NotificationType.ItemUpdated,
            MediaId = mediaId
        });
    }

    public void NotifyScanProgress(Guid libraryId, int processed, int total, string status)
    {
        _channel.Writer.TryWrite(new Notification
        {
            Type = NotificationType.ScanProgress,
            LibraryId = libraryId,
            Processed = processed,
            Total = total,
            Status = status
        });
    }

    public void NotifyLibraryRecentUpdated(Guid libraryId)
    {
        _channel.Writer.TryWrite(new Notification
        {
            Type = NotificationType.LibraryRecentUpdated,
            LibraryId = libraryId
        });
    }



    private async Task ProcessLoopAsync()
    {
        var batch = new Dictionary<string, Notification>();
        
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                // Wait for 500ms before processing batch
                await Task.Delay(500, _cts.Token);

                // Drain queue into batch (deduplicate by key)
                while (_channel.Reader.TryRead(out var notification))
                {
                    var key = notification.Type switch
                    {
                        NotificationType.ItemUpdated => $"update-{notification.MediaId}",
                        NotificationType.ItemAdded => $"add-{notification.LibraryId}-{notification.MediaId}",
                        NotificationType.ScanProgress => $"progress-{notification.LibraryId}",
                        NotificationType.LibraryRecentUpdated => $"recent-{notification.LibraryId}",
                        _ => Guid.NewGuid().ToString()
                    };
                    batch[key] = notification; // Latest wins
                }

                // Send batched notifications
                foreach (var notification in batch.Values)
                {
                    await SendNotificationAsync(notification);
                }
                
                if (batch.Count > 0)
                {
                    _logger.LogDebug("Sent {Count} batched notifications", batch.Count);
                }
                
                batch.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in notification processing loop");
        }
    }

    private async Task SendNotificationAsync(Notification n)
    {
        try
        {
            switch (n.Type)
            {
                case NotificationType.ItemAdded:
                    await _hubContext.Clients.Group($"library-{n.LibraryId}")
                        .SendAsync("ItemAdded", n.LibraryId.ToString(), n.MediaId.ToString(), n.ItemType, n.Title);
                    _logger.LogDebug("Pushed ItemAdded: {Title} to library-{LibraryId}", n.Title, n.LibraryId);
                    break;

                case NotificationType.ItemUpdated:
                    await _hubContext.Clients.Group($"media-{n.MediaId}")
                        .SendAsync("ItemUpdated", n.MediaId.ToString());
                    _logger.LogDebug("Pushed ItemUpdated: {MediaId}", n.MediaId);
                    break;

                case NotificationType.ScanProgress:
                    await _hubContext.Clients.All
                        .SendAsync("ScanProgress", n.LibraryId.ToString(), n.Processed, n.Total, n.Status);
                    break;

                case NotificationType.LibraryRecentUpdated:
                    await _hubContext.Clients.Group($"library-{n.LibraryId}")
                        .SendAsync("LibraryRecentUpdated", n.LibraryId.ToString());
                    _logger.LogDebug("Pushed LibraryRecentUpdated to library-{LibraryId}", n.LibraryId);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send {Type} notification", n.Type);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _channel.Writer.Complete();
        try
        {
            _processTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch { }
        _cts.Dispose();
    }

    private enum NotificationType
    {
        ItemAdded,
        ItemUpdated,
        ScanProgress,
        LibraryRecentUpdated
    }

    private record Notification
    {
        public NotificationType Type { get; init; }
        public Guid LibraryId { get; init; }
        public Guid MediaId { get; init; }
        public string ItemType { get; init; } = "";
        public string Title { get; init; } = "";
        public int Processed { get; init; }
        public int Total { get; init; }
        public string Status { get; init; } = "";
    }
}
