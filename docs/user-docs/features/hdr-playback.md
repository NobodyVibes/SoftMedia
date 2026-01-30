# HDR Playback

SoftMedia provides advanced support for High Dynamic Range (HDR) video, ensuring you get the best possible colors on your display while maintaining compatibility where needed.

## HDR Modes

Depending on your hardware and settings, SoftMedia will play HDR content in one of two modes:

### native HDR (Passthrough)
The original HDR data is sent directly to your screen. This preserves the full range of colors and brightness.
- **Requirement**: An HDR-capable monitor/TV and a browser that supports HDR metadata.
- **Indicator**: No special icons are shown in the player; colors will appear vibrant and deep.

### SDR Tone Mapping
If your screen doesn't support HDR, or if specific features (like subtitles) require it, SoftMedia will convert the HDR video to Standard Dynamic Range (SDR) on-the-fly.
- **Why?**: Prevents the video from looking washed-out or "grey" on non-HDR screens.
- **Accuracy**: SoftMedia uses professional-grade tone mapping to preserve as much detail as possible.

## In-Player Notifications

SoftMedia keeps you informed about your HDR status through premium in-player notifications. These appear at the top of the video with a circular timer.

- **"HDR tone-mapping applied..."**: This info toast appears when the server switches to SDR mode to ensure compatibility (usually for subtitles).
- **"HDR passthrough re-enabled"**: This appears when you stop using a feature that required tone mapping, and the player has returned to native HDR mode.

> [!TIP]
> You can click the **X** inside the notification circle to dismiss it early, or simply wait for it to fade away after 8 seconds.

## The Subtitle Impact

When playing HDR movies, you may notice the screen "reload" or show a notification when you turn on subtitles.

- **Text Subtitles**: No impact. HDR is preserved.
- **Image Subtitles (PGS/Blu-ray)**: These must be merged into the video by the server. Because merging images into an HDR stream is technically complex and often results in poor visual quality, SoftMedia converts the video to **SDR Tone Mapping** while these subtitles are active.
- **Automatic Switching**: As soon as you turn these subtitles off, SoftMedia will automatically attempt to switch back to native HDR playback.

## Troubleshooting

If your video looks washed-out:
1. Open the **Settings** (Gear icon) in the player.
2. Check the **Burn Subtitles** setting.
3. Ensure your display is correctly configured for HDR in your OS settings.
4. Use the **Debug Panel** (press **D**) to see if "HDR Tone Mapping" is active.
