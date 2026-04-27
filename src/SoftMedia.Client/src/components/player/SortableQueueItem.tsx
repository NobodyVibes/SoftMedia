import { useSortable } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { GripVertical } from 'lucide-react';
import { cn } from '../../lib/utils';
import { API_URL } from '../../services/api';
import type { MediaItem } from '../../types';
import { ScrollingText } from '../ui/ScrollingText';
import { attachAuthToApiUrl } from '../../lib/mediaImageUrl';

interface Props {
    track: MediaItem;
    originalIndex: number; // The logic index in the queue
    id: number; // The sortable ID (same as index)
    onPlay: () => void;
}

export const SortableQueueItem = ({ track, originalIndex, id, onPlay }: Props) => {
    const {
        attributes,
        listeners,
        setNodeRef,
        transform,
        transition,
        isDragging
    } = useSortable({ id });

    const style = {
        transform: CSS.Transform.toString(transform),
        transition,
        zIndex: isDragging ? 50 : 'auto',
        position: 'relative' as const,
    };

    const getImageUrl = (path: string | undefined) => {
        if (!path) return '/placeholder-music.png';
        if (path.startsWith('/api/')) return attachAuthToApiUrl(path);
        if (path.startsWith('http')) return path;
        return `${API_URL}${path}`;
    };

    return (
        <div
            ref={setNodeRef}
            style={style}
            className={cn(
                "flex items-center gap-2 px-3 py-3 hover:bg-white/5 transition group touch-none select-none",
                isDragging && "opacity-50 bg-gray-800 shadow-xl rounded-lg"
            )}
        >
            <div
                {...attributes}
                {...listeners}
                className="text-gray-600 hover:text-gray-300 cursor-grab active:cursor-grabbing"
                title="Drag to reorder"
            >
                <GripVertical size={16} />
            </div>

            <div
                role="button"
                tabIndex={0}
                aria-label={`Play ${track.title ?? 'track'}`}
                onClick={onPlay}
                onKeyDown={(e) => {
                    if (e.key === 'Enter' || e.key === ' ') {
                        e.preventDefault();
                        onPlay();
                    }
                }}
                className="flex-1 flex items-center gap-3 cursor-pointer min-w-0 focus-visible:ring-2 focus-visible:ring-primary focus-visible:outline-none rounded"
            >
                <span className="text-gray-600 text-xs w-5 text-center shrink-0">
                    {originalIndex + 1}
                </span>

                <img
                    src={getImageUrl(track.posterPath)}
                    referrerPolicy="no-referrer"
                    alt={track.title}
                    className="w-10 h-10 rounded object-cover bg-gray-800 pointer-events-none"
                />
                <div className="flex-1 min-w-0">
                    <ScrollingText text={track.title} className="text-sm font-medium text-white" hoverOnly />
                    <p className="text-gray-400 text-xs truncate">
                        {(track.metadata?.artist as string) || 'Unknown'}
                    </p>
                </div>
            </div>
        </div>
    );
};
