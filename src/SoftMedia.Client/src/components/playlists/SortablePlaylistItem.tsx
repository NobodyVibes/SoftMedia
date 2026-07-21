import { useSortable } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { GripVertical, Trash2, Play } from 'lucide-react';
import { cn } from '../../lib/utils';
import { attachAuthToApiUrl } from '../../lib/mediaImageUrl';
import { ScrollingText } from '../ui/ScrollingText';
import type { PlaylistEntry } from '../../services/playlistService';
import { useMediaTokenRefresh } from '../../hooks/useMediaTokenRefresh';

interface SortablePlaylistItemProps {
    entry: PlaylistEntry;
    /** 1-based displayed position. */
    position: number;
    onPlay: () => void;
    onRemove?: () => void;
    canEdit: boolean;
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
 */
export function SortablePlaylistItem({ entry, position, onPlay, onRemove, canEdit }: SortablePlaylistItemProps) {
    // Media URLs below embed the media token; re-render when it rotates so a
    // stale token can't leave the artwork permanently broken.
    useMediaTokenRefresh();
    const {
        attributes,
        listeners,
        setNodeRef,
        transform,
        transition,
        isDragging,
    } = useSortable({ id: entry.playlistItemId });

    const style = {
        transform: CSS.Transform.toString(transform),
        transition,
        zIndex: isDragging ? 50 : 'auto',
        position: 'relative' as const,
    };

    const track = entry.media;

    const getImageUrl = (path: string | undefined) => {
        if (!path) return '/placeholder-music.png';
        if (path.startsWith('/api/')) return attachAuthToApiUrl(path);
        if (path.startsWith('http')) return path;
        // Anything else is a static file served from wwwroot (e.g.
        // /cache/images/albums/x.jpg). It is NOT under /api/v1 — prefixing it
        // there yields a route that does not exist and a 404'd cover.
        return path;
    };

    return (
        <div
            ref={setNodeRef}
            style={style}
            className={cn(
                'group flex items-center gap-3 px-3 py-3 rounded-lg transition touch-none select-none min-h-[56px]',
                'hover:bg-white/5 focus-within:bg-white/5',
                isDragging && 'opacity-60 bg-white/10 shadow-2xl ring-1 ring-primary/50'
            )}
        >
            {canEdit ? (
                <button
                    type="button"
                    {...attributes}
                    {...listeners}
                    aria-label="Drag to reorder"
                    className="text-gray-500 hover:text-gray-200 focus-visible:text-gray-200 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded p-2 cursor-grab active:cursor-grabbing min-w-[44px] min-h-[44px] flex items-center justify-center"
                >
                    <GripVertical size={16} />
                </button>
            ) : (
                <div className="w-[44px]" aria-hidden="true" />
            )}

            <button
                type="button"
                onClick={onPlay}
                className="flex-1 flex items-center gap-3 text-left min-w-0 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded-lg p-1 -m-1"
                aria-label={`Play ${track.title}`}
            >
                <span className="text-xs text-gray-500 w-6 text-center shrink-0 group-hover:hidden">
                    {position}
                </span>
                <span className="hidden group-hover:flex w-6 shrink-0 items-center justify-center text-primary">
                    <Play className="w-3.5 h-3.5 fill-primary" />
                </span>
                <img
                    src={getImageUrl(track.posterPath)}
                    referrerPolicy="no-referrer"
                    alt=""
                    className="w-12 h-12 rounded object-cover bg-gray-800 shrink-0 pointer-events-none"
                />
                <div className="flex-1 min-w-0">
                    <ScrollingText text={track.title} className="text-sm font-medium text-white" hoverOnly />
                    <div className="text-xs text-gray-400 truncate mt-0.5">
                        {(track.metadata?.artist as string) || 'Unknown artist'}
                    </div>
                </div>
            </button>

            {canEdit && onRemove && (
                <button
                    type="button"
                    onClick={onRemove}
                    aria-label="Remove from playlist"
                    className="text-gray-500 hover:text-red-400 focus-visible:text-red-400 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded p-2 opacity-0 group-hover:opacity-100 focus-visible:opacity-100 transition-opacity min-w-[44px] min-h-[44px] flex items-center justify-center"
                >
                    <Trash2 size={16} />
                </button>
            )}
        </div>
    );
}
