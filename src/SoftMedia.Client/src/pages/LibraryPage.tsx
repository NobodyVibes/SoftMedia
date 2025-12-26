import { useState, useEffect, useRef } from 'react';
import { useParams } from 'react-router-dom';
import { useQuery, useInfiniteQuery, useQueryClient } from '@tanstack/react-query';
import { useInView } from 'react-intersection-observer';
import api from '../services/api';
import HoverableMediaCardWrapper from '../components/items/HoverableMediaCardWrapper';
import { FilterBar } from '../components/library/FilterBar';
import { type MediaItem, type PagedResult, type Library, type LibraryScanJob } from '../types';
import { libraryService } from '../services/libraryService';

export default function LibraryPage() {
    const { ref, inView } = useInView();
    const { id } = useParams<{ id: string }>();
    const [hoveredId, setHoveredId] = useState<string | null>(null);
    const queryClient = useQueryClient();
    const prevScanStatusRef = useRef<Map<string, string>>(new Map());

    // Fetch Library Details
    const { data: library } = useQuery({
        queryKey: ['library', id],
        queryFn: async () => {
            const res = await api.get<Library>(`/libraries/${id}`);
            return res.data;
        },
        enabled: !!id
    });

    // Poll scan queue to detect when this library's scan completes
    const { data: scanQueue = [] } = useQuery<LibraryScanJob[]>({
        queryKey: ['scanQueue'],
        queryFn: libraryService.getScanQueue,
        refetchInterval: 2000, // Poll every 2s to catch scans of all speeds
    });

    // Detect scan completion and invalidate library items cache
    useEffect(() => {
        if (!id) return;

        scanQueue.forEach((job) => {
            if (job.libraryId === id) {
                const alreadyProcessed = prevScanStatusRef.current.has(job.id);
                const prevStatus = prevScanStatusRef.current.get(job.id);

                // If this is a new completed job we haven't seen before
                if (!alreadyProcessed && job.status === 'Completed') {
                    queryClient.invalidateQueries({ queryKey: ['library', id, 'items'] });
                    prevScanStatusRef.current.set(job.id, 'processed');
                }
                // If scan transitioned to Completed (was Running or Queued)
                else if (alreadyProcessed && prevStatus !== 'Completed' && prevStatus !== 'processed' && job.status === 'Completed') {
                    queryClient.invalidateQueries({ queryKey: ['library', id, 'items'] });
                    prevScanStatusRef.current.set(job.id, 'processed');
                }
                // First time seeing this job in any status
                else if (!alreadyProcessed) {
                    prevScanStatusRef.current.set(job.id, job.status);
                }
                // Status changed but not to Completed
                else if (prevStatus !== job.status && prevStatus !== 'processed') {
                    prevScanStatusRef.current.set(job.id, job.status);
                }
            }
        });
    }, [scanQueue, id, queryClient]);

    // Filter State
    const [search, setSearch] = useState('');
    const [sortBy, setSortBy] = useState('title');
    const [genre, setGenre] = useState('');
    const [year, setYear] = useState<number | null>(null);
    const [minRating, setMinRating] = useState<number | null>(null);
    const [isFavorite, setIsFavorite] = useState<boolean | null>(null);
    const [watched, setWatched] = useState<boolean | null>(null);
    const [viewMode, setViewMode] = useState('artists'); // Default to artists for Music

    const {
        data,
        isLoading,
        error,
        fetchNextPage,
        hasNextPage,
        isFetchingNextPage
    } = useInfiniteQuery({
        queryKey: ['library', id, 'items', { search, sortBy, genre, year, minRating, isFavorite, watched, viewMode }],
        queryFn: async ({ pageParam = 1 }) => {
            const params: Record<string, string | number | boolean | undefined | null> = {
                page: pageParam,
                pageSize: 50,
                search,
                sortBy,
                genre,
                year,
                minRating,
                isFavorite,
                watched,
                viewMode: library?.type === 'Music' ? viewMode : undefined
            };
            // Clean undefined/null params
            Object.keys(params).forEach(key => (params[key] === null || params[key] === '') && delete params[key]);

            const response = await api.get<PagedResult<MediaItem>>(`/libraries/${id}/items`, { params });
            return response.data;
        },
        getNextPageParam: (lastPage: PagedResult<MediaItem>) => {
            if (lastPage.items.length < lastPage.pageSize) return undefined;
            return lastPage.page + 1;
        },
        initialPageParam: 1,
        enabled: !!id
    });

    useEffect(() => {
        if (inView && hasNextPage) {
            fetchNextPage();
        }
    }, [inView, hasNextPage, fetchNextPage]);

    const allItems = data?.pages.flatMap((page: PagedResult<MediaItem>) => page.items) || [];

    // Render content area based on state
    const renderContent = () => {
        if (isLoading) {
            return (
                <div className="flex-1 flex items-center justify-center">
                    <div className="text-center text-gray-400">
                        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-primary mx-auto mb-4"></div>
                        Loading library...
                    </div>
                </div>
            );
        }

        if (error) {
            return (
                <div className="flex-1 flex items-center justify-center">
                    <div className="text-center text-red-400">Error loading library.</div>
                </div>
            );
        }

        if (allItems.length === 0) {
            return (
                <div className="flex-1 px-6 pt-6 pb-8">
                    <div className="text-center text-gray-500 mt-12">
                        <p className="text-xl">No items found.</p>
                        <p className="text-sm">Try adjusting your filters.</p>
                    </div>
                </div>
            );
        }

        return (
            <div className="flex-1 px-6 pt-6 pb-8">
                {/* CSS Grid for fixed positions - prevents reflow on hover */}
                <div
                    className="grid gap-6 justify-center"
                    style={{
                        gridTemplateColumns: 'repeat(auto-fill, 180px)',
                        justifyContent: 'center'
                    }}
                >
                    {allItems.map((item: MediaItem) => (
                        <HoverableMediaCardWrapper
                            key={item.id}
                            item={item}
                            hoveredId={hoveredId}
                            setHoveredId={setHoveredId}
                            libraryType={library?.type}
                        />
                    ))}
                </div>

                <div ref={ref} className="h-20 flex justify-center items-center mt-8">
                    {isFetchingNextPage && (
                        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-primary"></div>
                    )}
                </div>
            </div>
        );
    };

    return (
        <div className="min-h-screen bg-background -m-6 flex flex-col">
            {/* FilterBar - ALWAYS rendered to preserve state */}
            <FilterBar
                onSearch={setSearch}
                onSort={setSortBy}
                onGenre={setGenre}
                onYear={setYear}
                onRating={setMinRating}
                onFavorite={setIsFavorite}
                onWatched={setWatched}
                showWatchedFilter={library?.type === 'TV' || library?.type === 'Movie'}
                viewMode={library?.type === 'Music' ? viewMode : undefined}
                onViewModeChange={library?.type === 'Music' ? setViewMode : undefined}
                libraryType={library?.type}
                libraryId={id}
            />

            {/* Content area - rendered conditionally based on loading/error state */}
            {renderContent()}
        </div>
    );
}

