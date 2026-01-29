# Client Settings

SoftMedia allows you to configure playback preferences and quality settings specific to your device. These settings are stored locally in your browser and are isolated by your User ID.

## General Tab

The General tab focuses on your language and subtitle preferences.

### Language & Subtitles

These settings control how SoftMedia selects audio and subtitle tracks for you automatically.

**Settings Tree:** `Settings > Client Settings > General > Language & Subtitles`

1.  **Audio Language**
    *   **Description**: Sets your preferred language for audio tracks.
    *   **Purpose**: SoftMedia will automatically select the audio track that matches this language when you start a video. If your preferred language isn't available, it will fall back to the file's default track.

2.  **Subtitle Language**
    *   **Description**: Sets your preferred language for subtitles.
    *   **Purpose**: Controls automatic subtitle selection.
        *   **Off**: Subtitles will never turn on automatically.
        *   **Language (e.g., English)**: Subtitles in this language will be enabled automatically for all videos, regardless of audio language. You can manually turn them "Off" for specific shows, and SoftMedia will remember that preference.

> [!NOTE]
> sometimes multiple subtitle tracks may exist for a single language. If subtitles dont work for the selected language, try the other. Once the correct track is selected for a TV Show or Movie, SoftMedia will remember that preference for that TV Show or Movie.

## Playback Tab

The Playback tab allows you to manage streaming quality and data usage client side.

### Streaming Quality

**Settings Tree:** `Settings > Client Settings > Playback > Streaming Quality`

1.  **Default Quality**
    *   **Description**: Sets your preferred video resolution (e.g., 720p, 1080p, 4K, Auto).
    *   **Purpose**: SoftMedia attempts to stream at this resolution by default. "Auto" uses the Server's configured default quality (usually 1080p), while "Original" plays the file as-is without transcoding if possible.

2.  **Max Bitrate (kbps)**
    *   **Description**: Sets a maximum bandwidth limit for streaming.
    *   **Purpose**: Prevents the stream from exceeding a specific bitrate, which is useful for limited internet connections. This limit takes priority over your "Default Quality" if the quality requires more bandwidth than allowed.

3.  **Data Saver Mode**
    *   **Description**: A toggle to aggressively reduce data usage.
    *   **Purpose**: when enabled, this automatically limits playback to a maximum of **2 Mbps** and **720p** resolution. This overrides your other quality settings to ensure minimal data consumption, ideal for mobile data.
