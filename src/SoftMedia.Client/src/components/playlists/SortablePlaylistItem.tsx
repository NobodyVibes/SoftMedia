import { useState } from 'react';
import { useSortable } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { GripVertical, Trash2, Play, ListEnd, ListPlus, Volume2 } from 'lucide-react';
import { cn, formatDuration } from '../../lib/utils';
import { resolveArtworkUrl } from '../../lib/mediaImageUrl';
import { ScrollingText } from '../ui/ScrollingText';
import type { PlaylistEntry } from '../../services/playlistService';
import { useMediaTokenRefresh } from '../../hooks/useMediaTokenRefresh';
import { AddToPlaylistMenu } from './AddToPlaylistMenu';

interface SortablePlaylistItemProps {
    entry: PlaylistEntry;
    /** 1-based displayed position. */
    position: number;
    onPlay: () => void;
    onRemove?: () => void;
    /** Appends this track to the playback queue without touching the playlist. */
    onAddToQueue?: () => void;
    canEdit: boolean;
    /** Marks the row as the track the player is currently on. */
    isCurrent?: boolean;
    /**
     * Suppresses drag while a filter is applied: the visible rows are a subset,
     * so a drop would compute an order against positions the user can't see.
     */
    dragDisabled?: boolean;
}

/**
 * Wave E1 — single track row in a playlist with drag-to-reorder.
 *
 * Same component renders desktop (mouse) and touch (long-press handled by
 * @dnd-kit/sortable). Per universal-client rules: button semantics, paired
 * hover/focus, ≥44px hit target.
 *
 * The drag handle is only rendered when `canEdit === true`. Non-owners viewing
 * a public playlist can play tracks but cannot reorder them — drag handle is
 * the visible signal for that capability.
 *
 * Row actions sit at reduced opacity rather than fully hidden until hover: the
 * same reasoning as the album track list, that a hover-only affordance is
 * invisible to touch users, who have no hover to give.
 */
export function SortablePlaylistItem({
    entry,
    position,
    onPlay,
    onRemove,
    onAddToQueue,
    canEdit,
    isCurrent,
    dragDisabled,
}: SortablePlaylistItemProps) {
    // Media URLs below embed the media token; re-render when it rotates so a
    // stale token can't leave the artwork permanently broken.
    useMediaTokenRefresh();
    const [showPlaylistMenu, setShowPlaylistMenu] = useState(false);

    const {
        attributes,
        listeners,
        setNodeRef,
        transform,
        transition,
        isDragging,
    } = useSortable({ id: entry.playlistItemId, disabled: dragDisabled });

    const style = {
        transform: CSS.Transform.toString(transform),
        transition,
        zIndex: isDragging ? 50 : 'auto',
        position: 'relative' as const,
    };

    const track = entry.media;
    const artist = (track.metadata?.artist as string) || 'Unknown artist';
    const album = track.metadata?.album as string | undefined;
    const duration = track.durationSeconds ? formatDuration(track.durationSeconds) : null;

    const getImageUrl = resolveArtworkUrl; // shared; /cache/images is token-gated (AA-WI-001)

    const iconButton =
        'rounded p-2 opacity-60 hover:opacity-100 focus-visible:opacity-100 focus-visible:outline-none ' +
        'focus-visible:ring-2 focus-visible:ring-blue-400 transition min-w-[44px] min-h-[44px] flex items-center justify-center';

    return (
        <div
            ref={setNodeRef}
            style={style}
            className={cn(
                'group flex items-center gap-2 sm:gap-3 px-3 py-2 rounded-lg transition touch-none select-none min-h-[60px]',
                'hover:bg-white/5 focus-within:bg-white/5',
                isCurrent && 'bg-primary/10',
                isDragging && 'opacity-60 bg-white/10 shadow-2xl ring-1 ring-primary/50'
            )}
        >
            {canEdit && !dragDisabled ? (
                <button
                    type="button"
                    {...attributes}
                    {...listeners}
                    aria-label="Drag to reorder"
                    // Stays visible on touch: the TouchSensor's long-press exists so
                    // phones can reorder too, and hiding the handle there would take
                    // that away with no replacement.
                    className="text-gray-500 hover:text-gray-200 focus-visible:text-gray-200 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded p-2 cursor-grab active:cursor-grabbing min-w-[44px] min-h-[44px] flex items-center justify-center"
                >
                    <GripVertical size={16} />
                </button>
            ) : (
                <div className="w-0 sm:w-[44px]" aria-hidden="true" />
            )}

            <button
                type="button"
                onClick={onPlay}
                className="flex-1 flex items-center gap-3 text-left min-w-0 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded-lg p-1 -m-1"
                aria-label={`Play ${track.title}`}
            >
                {/* The playing row keeps a persistent indicator instead of a
                    number, so the current track stays identifiable while the
                    pointer is elsewhere in the list. */}
                <span className="w-6 shrink-0 flex items-center justify-center">
                    {isCurrent ? (
                        <Volume2 className="w-3.5 h-3.5 text-primary" />
                    ) : (
                        <>
                            <span className="text-xs text-gray-500 group-hover:hidden">{position}</span>
                            <Play className="w-3.5 h-3.5 fill-primary text-primary hidden group-hover:block" />
                        </>
                    )}
                </span>
                {/* Lazy + explicit dimensions: long playlists are the known pain
                    point here (lists of 800+ tracks), and an eager cover on every
                    row means hundreds of image requests competing for connection
                    slots the moment the page opens. The fixed box also stops rows
                    reflowing as art arrives, which matters mid-drag. */}
                <img
                    src={getImageUrl(track.posterPath)}
                    referrerPolicy="no-referrer"
                    loading="lazy"
                    decoding="async"
                    width={48}
                    height={48}
                    alt=""
                    className="w-12 h-12 rounded object-cover bg-gray-800 shrink-0 pointer-events-none"
                />
                <div className="flex-1 min-w-0">
                    <ScrollingText
                        text={track.title}
                        className={cn('text-sm font-medium', isCurrent ? 'text-primary' : 'text-white')}
                        hoverOnly
                    />
                    <div className="text-xs text-gray-400 truncate mt-0.5">
                        {album ? `${artist} · ${album}` : artist}
                    </div>
                </div>
            </button>

            {duration && (
                <span className="text-xs text-gray-500 tabular-nums shrink-0 hidden sm:block">
                    {duration}
                </span>
            )}

            {onAddToQueue && (
                <button
                    type="button"
                    onClick={onAddToQueue}
                    aria-label={`Add ${track.title} to queue`}
                    title="Add to queue"
                    className={cn(iconButton, 'text-gray-400 hover:text-white focus-visible:text-white hover:bg-white/10 hidden sm:flex')}
                >
                    <ListEnd size={16} />
                </button>
            )}

            {/* Copying a track into another playlist — the index page has no way
                to do this, so without it a track can only ever live where it was
                first added. */}
            <div className="relative hidden sm:block">
                <button
                    type="button"
                    data-add-to-playlist-trigger
                    onClick={() => setShowPlaylistMenu(v => !v)}
                    aria-label={`Add ${track.title} to a playlist`}
                    aria-haspopup="menu"
                    aria-expanded={showPlaylistMenu}
                    title="Add to playlist"
                    className={cn(iconButton, 'text-gray-400 hover:text-white focus-visible:text-white hover:bg-white/10')}
                >
                    <ListPlus size={16} />
                </button>
                {showPlaylistMenu && (
                    <AddToPlaylistMenu
                        mediaItemIds={[track.id]}
                        onClose={() => setShowPlaylistMenu(false)}
                    />
                )}
            </div>

            {canEdit && onRemove && (
                <button
                    type="button"
                    onClick={onRemove}
                    aria-label="Remove from playlist"
                    title="Remove from playlist"
                    className={cn(iconButton, 'text-gray-500 hover:text-red-400 focus-visible:text-red-400 hover:bg-red-500/10')}
                >
                    <Trash2 size={16} />
                </button>
            )}
        </div>
    );
}
