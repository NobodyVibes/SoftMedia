import { useSortable } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { GripVertical } from 'lucide-react';
import { cn } from '../../lib/utils';
import { API_URL } from '../../services/api';
import type { MediaItem } from '../../types';

interface Props {
    track: MediaItem;
    originalIndex: number; // The logic index in the queue
    id: number; // The sortable ID (same as index)
    isPreloaded: boolean;
    onPlay: () => void;
}

export const SortableQueueItem = ({ track, originalIndex, id, isPreloaded, onPlay }: Props) => {
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
        if (path.startsWith('/api/')) return path;
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
                onClick={onPlay}
                className="flex-1 flex items-center gap-3 cursor-pointer min-w-0"
            >
                <span className="text-gray-600 text-xs w-5 text-center shrink-0">
                    {originalIndex + 1}
                </span>

                <img
                    src={getImageUrl(track.posterPath)}
                    alt={track.title}
                    className="w-10 h-10 rounded object-cover bg-gray-800 pointer-events-none"
                />
                <div className="flex-1 min-w-0">
                    <p className={cn("truncate text-sm font-medium", isPreloaded ? "text-primary" : "text-white")}>
                        {track.title}
                    </p>
                    <p className="text-gray-400 text-xs truncate">
                        {(track.metadata?.artist as string) || 'Unknown'}
                    </p>
                </div>
            </div>

            {isPreloaded && (
                <span className="text-xs text-primary shrink-0 font-medium bg-primary/10 px-2 py-1 rounded">Ready</span>
            )}
        </div>
    );
};
