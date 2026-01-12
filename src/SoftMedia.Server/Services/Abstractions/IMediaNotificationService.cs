namespace SoftMedia.Server.Services.Abstractions;

/// <summary>
/// Service for pushing real-time notifications to connected clients via SignalR.
/// </summary>
public interface IMediaNotificationService
{
    /// <summary>
    /// Notify clients that a new item was added to a library.
    /// </summary>
    void NotifyItemAdded(Guid libraryId, Guid itemId, string itemType, string title);

    /// <summary>
    /// Notify clients that a media item's metadata or images were updated.
    /// </summary>
    void NotifyItemUpdated(Guid mediaId);

    /// <summary>
    /// Notify clients of scan progress for a library.
    /// </summary>
    void NotifyScanProgress(Guid libraryId, int processed, int total, string status);
}
