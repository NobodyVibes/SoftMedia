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

interface Props {
    isPreloaded: boolean;
}

export const QueueList: React.FC<Props> = ({ isPreloaded }) => {
    const { queue, reorderQueue, jumpToQueueIndex } = useAudioStore();
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

    if (queue.length === 0) {
        return <p className="text-gray-500 text-sm text-center py-8">Queue is empty</p>;
    }

    // Determine if the first item is "Ready" (preloaded)
    // Actually, `PersistentPlayer` manages `isPreloaded` state.
    // It's hard to sync that state here without passing it down.
    // But `PersistentPlayer` implementation of `isPreloaded` is logic-heavy.
    // However, the visual "Ready" badge was only for index 0.
    // Maybe we omit it for now or pass it as a prop?
    // Let's omit "Ready" badge logic from QueueList for now or assume it's lost during reorder?
    // User requested "Ready" badge in previous tasks.
    // I should probably pass `isPreloaded` as a prop to `QueueList`.
    // But `QueueList` is consuming store.
    // I'll add `isPreloaded` to Props.

    return (
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
                <div className="flex-1 overflow-y-auto">
                    {queue.map((track, index) => (
                        <SortableQueueItem
                            key={`${track.id}-${index}`} // Composite key for React
                            id={index} // Sortable ID is the index
                            originalIndex={index}
                            track={track}
                            isPreloaded={index === 0 && isPreloaded}
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
                            isPreloaded={false}
                            onPlay={() => { }}
                        />
                    </div>
                ) : null}
            </DragOverlay>
        </DndContext>
    );
};
