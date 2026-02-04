import React, { useMemo } from 'react';
import {
    DndContext,
    closestCenter,
    KeyboardSensor,
    PointerSensor,
    useSensor,
    useSensors,
    DragOverlay
} from '@dnd-kit/core';
import type { DragEndEvent, DragStartEvent } from '@dnd-kit/core';
import {
    SortableContext,
    sortableKeyboardCoordinates,
    verticalListSortingStrategy
} from '@dnd-kit/sortable';
import { useAudioStore } from '../../store/audioStore';
import { SortableQueueItem } from './SortableQueueItem';
import { API_URL } from '../../services/api';
import { Volume2 } from 'lucide-react';
import { ScrollingText } from '../ui/ScrollingText';

export const QueueList: React.FC = () => {
    const { queue, currentTrack, reorderQueue, jumpToQueueIndex } = useAudioStore();
    const [activeId, setActiveId] = React.useState<number | null>(null);

    const sensors = useSensors(
        useSensor(PointerSensor, {
            activationConstraint: {
                distance: 8,
            },
        }),
        useSensor(KeyboardSensor, {
            coordinateGetter: sortableKeyboardCoordinates,
        })
    );

    const items = useMemo(() => queue.map((_, i) => i), [queue.length]);

    const handleDragStart = (event: DragStartEvent) => {
        setActiveId(event.active.id as number);
    };

    const handleDragEnd = (event: DragEndEvent) => {
        const { active, over } = event;
        setActiveId(null);

        if (over && active.id !== over.id) {
            const oldIndex = active.id as number;
            const newIndex = over.id as number;
            reorderQueue(oldIndex, newIndex);
        }
    };

    if (queue.length === 0 && !currentTrack) {
        return <p className="text-gray-500 text-sm text-center py-8">Queue is empty</p>;
    }

    const getImageUrl = (path: string | undefined) => {
        if (!path) return '/placeholder-music.png';
        if (path.startsWith('/api/')) return path;
        if (path.startsWith('http')) return path;
        return `${API_URL}${path}`;
    };

    return (
        <div className="flex-1 overflow-y-auto">
            {currentTrack && (
                <div className="sticky top-0 z-10 bg-gray-900/95 backdrop-blur border-b border-gray-800">
                    <div className="flex items-center gap-2 px-3 py-3 select-none">
                        <div className="w-6 flex justify-center text-primary shrink-0">
                            <Volume2 size={16} />
                        </div>

                        <div className="flex-1 flex items-center gap-3 min-w-0">
                            <img
                                src={getImageUrl(currentTrack.posterPath)}
                                alt={currentTrack.title}
                                className="w-10 h-10 rounded object-cover bg-gray-800 shadow-md"
                            />
                            <div className="flex-1 min-w-0">
                                <ScrollingText text={currentTrack.title} className="text-sm font-bold text-primary" />
                                <p className="text-gray-400 text-xs truncate">
                                    {(currentTrack.metadata?.artist as string) || 'Unknown'}
                                </p>
                            </div>
                        </div>

                        <span className="text-[10px] text-white bg-gradient-to-r from-[#007AFF] to-[#8A2BE2] px-2 py-0.5 rounded-full font-bold uppercase tracking-wider">
                            Playing
                        </span>
                    </div>
                </div>
            )}

            <DndContext
                sensors={sensors}
                collisionDetection={closestCenter}
                onDragStart={handleDragStart}
                onDragEnd={handleDragEnd}
            >
                <SortableContext
                    items={items}
                    strategy={verticalListSortingStrategy}
                >
                    <div className="pb-4">
                        {queue.map((track, index) => (
                            <SortableQueueItem
                                key={`${track.id}-${index}`}
                                id={index}
                                originalIndex={index}
                                track={track}
                                onPlay={() => jumpToQueueIndex(index)}
                            />
                        ))}
                    </div>
                </SortableContext>
                <DragOverlay>
                    {activeId !== null && queue[activeId] ? (
                        <div className="opacity-90 bg-gray-900 border border-gray-700 rounded-lg shadow-2xl">
                            <SortableQueueItem
                                id={activeId}
                                originalIndex={activeId}
                                track={queue[activeId]}
                                onPlay={() => { }}
                            />
                        </div>
                    ) : null}
                </DragOverlay>
            </DndContext>
        </div>
    );
};
