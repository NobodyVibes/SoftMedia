import { type MediaItem } from '../../types';
import { Music, MessageSquare, ChevronDown } from 'lucide-react';
import React from 'react';

interface MediaQualityInfoProps {
    item: MediaItem;
    className?: string;
}

/**
 * Displays extended quality metadata for video media items.
 * Shows video quality info (resolution, HDR, codec) and audio info (channels, codec).
 */
export const MediaQualityInfo: React.FC<MediaQualityInfoProps> = ({ item, className = '' }) => {
    if (!item) return null;

    // Only show for video types (Movie, Episode) - Series are virtual containers
    const isVideoType = item.type === 'Movie' || item.type === 'Episode';
    if (!isVideoType) return null;

    // Video quality label (e.g., "4K HDR10" or "1080p SDR")
    const getVideoQualityLabel = (): string => {
        const parts: string[] = [];

        // Resolution label
        if (item.height || item.width) {
            const h = item.height || 0;
            const w = item.width || 0;

            if (h >= 4300 || w >= 7600) parts.push('8K');
            else if (h >= 2100 || w >= 3800) parts.push('4K');
            else if (h >= 1400 || w >= 2500) parts.push('1440p');
            else if (h >= 1000 || w >= 1900) parts.push('1080p');
            else if (h >= 700 || w >= 1260) parts.push('720p');
            else if (h >= 480 || w >= 840) parts.push('480p');
            else if (h >= 360 || w >= 640) parts.push('360p');
            else if (h >= 240 || w >= 420) parts.push('240p');
            else if (h > 0) parts.push(`${h}p`);
            else if (w > 0) parts.push(`${w}w`);
        }

        // HDR format
        if (item.hdrFormat) {
            parts.push(item.hdrFormat);
        }

        return parts.join(' ') || 'Unknown';
    };

    // Bit depth label (e.g., "10-bit")
    const getBitDepthLabel = (): string | null => {
        if (!item.bitDepth) return null;
        return `${item.bitDepth}-bit`;
    };

    // Frame rate label (e.g., "23.976 fps")
    const getFrameRateLabel = (): string | null => {
        if (!item.frameRate) return null;
        // Round to 3 decimal places for common rates like 23.976
        const rounded = Math.round(item.frameRate * 1000) / 1000;
        return `${rounded} fps`;
    };

    // Audio channel layout label (e.g., "7.1 Atmos")
    const getAudioLabel = (): string => {
        const parts: string[] = [];

        if (item.audioChannels) {
            if (item.audioChannels === 2) parts.push('Stereo');
            else if (item.audioChannels === 6) parts.push('5.1');
            else if (item.audioChannels === 8) parts.push('7.1');
            else parts.push(`${item.audioChannels}ch`);
        }

        if (item.audioCodec) {
            const codec = item.audioCodec.toUpperCase();
            // Show friendly names for common codecs
            if (codec.includes('TRUEHD') || codec.includes('ATMOS')) parts.push('Atmos');
            else if (codec.includes('DTS')) parts.push('DTS');
            else if (codec.includes('EAC3') || codec.includes('E-AC-3')) parts.push('Dolby Digital+');
            else if (codec.includes('AC3') || codec.includes('AC-3')) parts.push('Dolby Digital');
            else if (codec.includes('AAC')) parts.push('AAC');
            else if (codec.includes('FLAC')) parts.push('FLAC');
            else parts.push(codec);
        }

        return parts.join(' ') || 'Unknown';
    };

    // Bitrate label (e.g., "25 Mbps")
    const getBitrateLabel = (): string | null => {
        if (!item.bitrate) return null;
        const mbps = item.bitrate / 1000000;
        if (mbps >= 1) {
            return `${mbps.toFixed(1)} Mbps`;
        }
        return `${(item.bitrate / 1000).toFixed(0)} kbps`;
    };

    // Track Dropdown Helper
    const TrackDropdown = ({
        icon: Icon,
        label,
        count,
        children
    }: {
        icon: any,
        label: string,
        count: number,
        children: React.ReactNode
    }) => (
        <div className="relative group">
            <div className="absolute inset-y-0 left-0 pl-3 flex items-center pointer-events-none">
                <Icon className="h-4 w-4 text-zinc-500 group-hover:text-white transition-colors" />
            </div>
            <select
                className="appearance-none bg-white/5 border border-white/10 hover:border-white/20 text-white text-sm rounded-lg pl-10 pr-8 py-2 focus:outline-none focus:ring-2 focus:ring-primary/50 cursor-pointer min-w-[140px] transition-all"
                value=""
                onChange={() => { }}
            >
                <option value="" disabled className="bg-zinc-900 text-zinc-500">
                    {count} {label}{count !== 1 ? 's' : ''}
                </option>
                {children}
            </select>
            <div className="absolute inset-y-0 right-0 pr-3 flex items-center pointer-events-none">
                <ChevronDown className="h-4 w-4 text-zinc-500" />
            </div>
        </div>
    );

    return (
        <div className={`flex flex-col gap-3 ${className}`}>
            {/* Row 1: Technical specs */}
            <div className="flex flex-wrap gap-4 text-sm text-zinc-400">
                {/* Video Quality */}
                <div className="flex items-center gap-2">
                    <span className="text-zinc-500">Video:</span>
                    <span className="text-white font-medium">{getVideoQualityLabel()}</span>
                    {item.videoCodec && (
                        <span className="text-zinc-500">({item.videoCodec.toUpperCase()})</span>
                    )}
                </div>

                {/* Bit Depth */}
                {getBitDepthLabel() && (
                    <div className="flex items-center gap-2">
                        <span className="text-zinc-500">Color:</span>
                        <span className="text-white">{getBitDepthLabel()}</span>
                    </div>
                )}

                {/* Frame Rate */}
                {getFrameRateLabel() && (
                    <div className="flex items-center gap-2">
                        <span className="text-zinc-500">Frame Rate:</span>
                        <span className="text-white">{getFrameRateLabel()}</span>
                    </div>
                )}

                {/* Audio */}
                <div className="flex items-center gap-2">
                    <span className="text-zinc-500">Audio:</span>
                    <span className="text-white font-medium">{getAudioLabel()}</span>
                </div>

                {/* Bitrate */}
                {getBitrateLabel() && (
                    <div className="flex items-center gap-2">
                        <span className="text-zinc-500">Bitrate:</span>
                        <span className="text-white">{getBitrateLabel()}</span>
                    </div>
                )}
            </div>

            {/* Row 2: Track Dropdowns */}
            {(item.audioTracks?.length || 0) > 0 || (item.subtitleTracks?.length || 0) > 0 ? (
                <div className="flex flex-wrap gap-3">
                    {/* Audio Tracks Dropdown */}
                    {(item.audioTracks?.length || 0) > 0 && (
                        <TrackDropdown
                            icon={Music}
                            label="Audio Track"
                            count={item.audioTracks!.length}
                        >
                            {item.audioTracks!.map((track, i) => (
                                <option key={i} value={i} className="bg-zinc-900">
                                    {track.language || 'Unknown'} - {track.codec?.toUpperCase() || 'Unknown'}
                                    {track.channels ? ` (${track.channels}ch)` : ''}
                                    {track.title ? ` - ${track.title}` : ''}
                                    {track.isDefault ? ' [Default]' : ''}
                                </option>
                            ))}
                        </TrackDropdown>
                    )}

                    {/* Subtitle Tracks Dropdown */}
                    {(item.subtitleTracks?.length || 0) > 0 && (
                        <TrackDropdown
                            icon={MessageSquare}
                            label="Subtitle"
                            count={item.subtitleTracks!.length}
                        >
                            {item.subtitleTracks!.map((track, i) => (
                                <option key={i} value={i} className="bg-zinc-900">
                                    {track.language || 'Unknown'} - {track.codec?.toUpperCase() || 'Text'}
                                    {track.title ? ` - ${track.title}` : ''}
                                    {track.isDefault ? ' [Default]' : ''}
                                    {track.isForced ? ' [Forced]' : ''}
                                </option>
                            ))}
                        </TrackDropdown>
                    )}
                </div>
            ) : null}
        </div>
    );
};

export default MediaQualityInfo;
