import { type MediaItem } from '../../types';
import { Music, MessageSquare, ChevronDown } from 'lucide-react';
import React, { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import api from '../../services/api';

interface MediaQualityInfoProps {
    item: MediaItem;
    className?: string;
    /**
     * DV-WI-020 (revised ×3) — controlled version selection. When provided, the
     * "Video:" dropdown reports picks upward so the PAGE can make the main Play button
     * honor them (play what you're looking at); without it the component keeps a local
     * selection (episode inspection on TV pages) that affects display only.
     */
    selectedVersionId?: string | null;
    onVersionSelect?: (versionId: string) => void;
}

/**
 * Displays extended quality metadata for video media items.
 * Shows video quality info (resolution, HDR, codec) and audio info (channels, codec).
 *
 * DV-WI-020 (revised ×3): when the title exists as multiple file copies, the "Video:"
 * VALUE is a version dropdown — picking a copy fetches that sibling item and the WHOLE
 * panel (codec, color depth, frame rate, audio incl. Atmos, bitrate, track lists) shows
 * that file's metadata before anything plays. With a controlled parent (movie detail
 * page) the pick also becomes the main Play button's target; the split-Play chevron
 * remains the explicit per-press override.
 */
/**
 * Track Dropdown Helper. Module-scoped, not declared inside MediaQualityInfo:
 * a component created during render gets a fresh identity every pass, so React
 * unmounts and remounts it (resetting DOM state like an open <select>) on each
 * parent re-render.
 */
function TrackDropdown({
    icon: Icon,
    label,
    count,
    children,
}: {
    icon: React.ComponentType<{ className?: string }>;
    label: string;
    count: number;
    children: React.ReactNode;
}) {
    return (
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
}

export const MediaQualityInfo: React.FC<MediaQualityInfoProps> = ({
    item, className = '', selectedVersionId, onVersionSelect,
}) => {
    // Hooks must run unconditionally — the type guard returns below them.
    const versions = item?.versions;
    const hasVersions = (versions?.length ?? 0) > 1;
    const controlled = !!onVersionSelect;
    const [internalSelectedId, setInternalSelectedId] = useState<string | null>(null);

    // Reset the local selection when the page swaps to a different item
    // (adjust-during-render pattern, same as MediaDetailPage's qualityItem reset).
    const [lastItemId, setLastItemId] = useState(item?.id);
    if (item && item.id !== lastItemId) {
        setLastItemId(item.id);
        setInternalSelectedId(null);
    }

    const effectiveSelectedId = (controlled ? selectedVersionId : internalSelectedId) ?? item?.id;

    // A picked sibling copy is a full MediaItem of its own — fetch it so the panel can
    // show its real probed metadata (audio tracks included, which is where Atmos lives).
    const wantsSibling = !!effectiveSelectedId && effectiveSelectedId !== item?.id;
    const { data: siblingItem, isFetching } = useQuery<MediaItem>({
        queryKey: ['media', effectiveSelectedId],
        queryFn: async () => (await api.get<MediaItem>(`/media/${effectiveSelectedId}`)).data,
        enabled: wantsSibling,
        staleTime: 60_000,
    });

    if (!item) return null;

    // Only show for video types (Movie, Episode) - Series are virtual containers
    const isVideoType = item.type === 'Movie' || item.type === 'Episode';
    if (!isVideoType) return null;

    // The item whose metadata the panel displays: the picked copy once loaded, else the
    // page's own item.
    const shown: MediaItem = (wantsSibling && siblingItem) ? siblingItem : item;

    // Video quality label (e.g., "4K HDR10" or "1080p SDR")
    const getVideoQualityLabel = (): string => {
        const parts: string[] = [];

        // Resolution label
        if (shown.height || shown.width) {
            const h = shown.height || 0;
            const w = shown.width || 0;

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
        if (shown.hdrFormat) {
            parts.push(shown.hdrFormat);
        }

        return parts.join(' ') || 'Unknown';
    };

    // Bit depth label (e.g., "10-bit")
    const getBitDepthLabel = (): string | null => {
        if (!shown.bitDepth) return null;
        return `${shown.bitDepth}-bit`;
    };

    // Frame rate label (e.g., "23.976 fps")
    const getFrameRateLabel = (): string | null => {
        if (!shown.frameRate) return null;
        // Round to 3 decimal places for common rates like 23.976
        const rounded = Math.round(shown.frameRate * 1000) / 1000;
        return `${rounded} fps`;
    };

    // Audio channel layout label (e.g., "7.1 Atmos")
    const getAudioLabel = (): string => {
        const parts: string[] = [];

        if (shown.audioChannels) {
            if (shown.audioChannels === 2) parts.push('Stereo');
            else if (shown.audioChannels === 6) parts.push('5.1');
            else if (shown.audioChannels === 8) parts.push('7.1');
            else parts.push(`${shown.audioChannels}ch`);
        }

        if (shown.audioCodec) {
            const codec = shown.audioCodec.toUpperCase();
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
        if (!shown.bitrate) return null;
        const mbps = shown.bitrate / 1000000;
        if (mbps >= 1) {
            return `${mbps.toFixed(1)} Mbps`;
        }
        return `${(shown.bitrate / 1000).toFixed(0)} kbps`;
    };

    return (
        <div className={`flex flex-col gap-3 ${className}`}>
            {/* Row 1: Technical specs (dimmed while a picked copy's metadata loads) */}
            <div className={`flex flex-wrap gap-4 text-sm text-zinc-400 transition-opacity ${isFetching ? 'opacity-50' : ''}`}>
                {/* Video Quality. With multiple copies the VALUE is the version dropdown —
                    picking one swaps the whole panel to that file's specs (and, on movie
                    pages, retargets the main Play button via the controlled parent). */}
                <div className="flex items-center gap-2">
                    <span className="text-zinc-500">Video:</span>
                    {hasVersions ? (
                        // Visibly a CONTROL, not text: chip background + border (the same
                        // visual language as the track dropdowns below, sized for the row)
                        // so users notice the versions are switchable here.
                        <span className="relative inline-flex items-center">
                            <select
                                aria-label="Video version"
                                value={effectiveSelectedId}
                                onChange={(e) => (controlled
                                    ? onVersionSelect!(e.target.value)
                                    : setInternalSelectedId(e.target.value))}
                                className="appearance-none bg-white/10 border border-white/20 hover:border-blue-400/60 hover:bg-white/15 text-white font-medium rounded-md pl-2.5 pr-7 py-1 cursor-pointer focus:outline-none focus:ring-2 focus:ring-blue-400/50 transition-all"
                            >
                                {versions!.map((v) => (
                                    <option key={v.id} value={v.id} className="bg-zinc-900 text-white">
                                        {v.label}{v.isPrimary ? ' [Default]' : ''}
                                    </option>
                                ))}
                            </select>
                            <ChevronDown className="w-3.5 h-3.5 text-zinc-300 absolute right-2 pointer-events-none" aria-hidden="true" />
                        </span>
                    ) : (
                        <span className="text-white font-medium">{getVideoQualityLabel()}</span>
                    )}
                    {shown.videoCodec && (
                        <span className="text-zinc-500">({shown.videoCodec.toUpperCase()})</span>
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
            {(shown.audioTracks?.length || 0) > 0 || (shown.subtitleTracks?.length || 0) > 0 ? (
                <div className={`flex flex-wrap gap-3 transition-opacity ${isFetching ? 'opacity-50' : ''}`}>
                    {/* Audio Tracks Dropdown */}
                    {(shown.audioTracks?.length || 0) > 0 && (
                        <TrackDropdown
                            icon={Music}
                            label="Audio Track"
                            count={shown.audioTracks!.length}
                        >
                            {shown.audioTracks!.map((track, i) => (
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
                    {(shown.subtitleTracks?.length || 0) > 0 && (
                        <TrackDropdown
                            icon={MessageSquare}
                            label="Subtitle"
                            count={shown.subtitleTracks!.length}
                        >
                            {shown.subtitleTracks!.map((track, i) => (
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
