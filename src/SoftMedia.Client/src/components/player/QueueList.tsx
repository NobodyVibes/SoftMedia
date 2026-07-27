import React, { useMemo, useRef } from 'react';
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
import { Volume2, ChevronLeft, ChevronRight } from 'lucide-react';
import { ScrollingText } from '../ui/ScrollingText';
import { attachAuthToApiUrl } from '../../lib/mediaImageUrl';
import { useMediaTokenRefresh } from '../../hooks/useMediaTokenRefresh';

/**
 * "Play all" on a prolific artist drops hundreds of tracks into the queue, and
 * every row mounts a dnd-kit sortable plus an artwork <img>. Rendering the lot
 * makes opening the queue visibly janky, so the list scrolls within a page and
 * pages beyond that.
 */
const PAGE_SIZE = 50;

export const QueueList: React.FC = () => {
    // Media URLs below embed the media token; re-render when it rotates so a
    // stale token can't leave the artwork permanently broken.
    useMediaTokenRefresh();
    const { queue, currentTrack, reorderQueue, jumpToQueueIndex } = useAudioStore();
    const [activeId, setActiveId] = React.useState<number | null>(null);
    const [requestedPage, setRequestedPage] = React.useState(0);
    const scrollRef = useRef<HTMLDivElement>(null);

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

    const totalPages = Math.max(1, Math.ceil(queue.length / PAGE_SIZE));
    // Clamp on read rather than syncing page state in an effect: the queue
    // shrinks as tracks play, so an effect would mean a setState chasing every
    // advance. `requestedPage` holds what the user asked for; `page` is what's
    // actually reachable right now.
    const page = Math.min(requestedPage, totalPages - 1);
    const pageStart = page * PAGE_SIZE;
    const pageEnd = Math.min(pageStart + PAGE_SIZE, queue.length);

    const pageTracks = useMemo(
        () => queue.slice(pageStart, pageEnd),
        [queue, pageStart, pageEnd]
    );

    // Sortable ids stay absolute queue indices so reorder/jump keep working —
    // and so row numbers read 51, 52, … on page two rather than restarting.
    const items = useMemo(
        () => pageTracks.map((_, i) => pageStart + i),
        [pageTracks, pageStart]
    );

    const goToPage = (next: number) => {
        setRequestedPage(Math.max(0, Math.min(next, totalPages - 1)));
        // Land at the top of the new page rather than wherever the last one was
        // scrolled to. Plain scrollTop, not scrollTo() — the latter isn't on
        // Element everywhere (jsdom included).
        if (scrollRef.current) scrollRef.current.scrollTop = 0;
    };

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
        if (path.startsWith('/api/')) return attachAuthToApiUrl(path);
        if (path.startsWith('http')) return path;
        // Anything else is a static file served from wwwroot (e.g.
        // /cache/images/albums/x.jpg). It is NOT under /api/v1 — prefixing it
        // there yields a route that does not exist and a 404'd cover.
        return path;
    };

    return (
        // min-h-0 on both levels: without it a flex child refuses to shrink below
        // its content, so the list overflows its panel instead of scrolling.
        <div className="flex-1 min-h-0 flex flex-col">
            <div ref={scrollRef} className="flex-1 min-h-0 overflow-y-auto">
                {currentTrack && (
                    <div className="sticky top-0 z-10 bg-gray-900/95 backdrop-blur border-b border-gray-800">
                        <div className="flex items-center gap-2 px-3 py-3 select-none">
                            <div className="w-6 flex justify-center text-primary shrink-0">
                                <Volume2 size={16} />
                            </div>

                            <div className="flex-1 flex items-center gap-3 min-w-0">
                                <img
                                    src={getImageUrl(currentTrack.posterPath)}
                                    referrerPolicy="no-referrer"
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
                            {pageTracks.map((track, i) => {
                                const index = pageStart + i;
                                return (
                                    <SortableQueueItem
                                        key={`${track.id}-${index}`}
                                        id={index}
                                        originalIndex={index}
                                        track={track}
                                        onPlay={() => {
                                            // Jumping rebuilds the queue from that
                                            // track, so page 2 would otherwise be
                                            // showing an unrelated stretch of it.
                                            setRequestedPage(0);
                                            jumpToQueueIndex(index);
                                        }}
                                    />
                                );
                            })}
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

            {totalPages > 1 && (
                <nav
                    aria-label="Queue pages"
                    className="shrink-0 flex items-center justify-between gap-2 border-t border-gray-800 bg-gray-900/95 px-3 py-2 backdrop-blur"
                >
                    <button
                        type="button"
                        onClick={() => goToPage(page - 1)}
                        disabled={page === 0}
                        aria-label="Previous page of queue"
                        className="rounded p-1 text-gray-400 transition hover:text-white focus-visible:text-white focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary disabled:pointer-events-none disabled:opacity-30"
                    >
                        <ChevronLeft size={16} />
                    </button>

                    <span className="text-[11px] text-gray-400 tabular-nums">
                        {pageStart + 1}&ndash;{pageEnd} of {queue.length}
                    </span>

                    <button
                        type="button"
                        onClick={() => goToPage(page + 1)}
                        disabled={page >= totalPages - 1}
                        aria-label="Next page of queue"
                        className="rounded p-1 text-gray-400 transition hover:text-white focus-visible:text-white focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary disabled:pointer-events-none disabled:opacity-30"
                    >
                        <ChevronRight size={16} />
                    </button>
                </nav>
            )}
        </div>
    );
};
