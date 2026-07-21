import { useState, useEffect } from 'react';
import { useParams } from 'react-router-dom';
import { useQuery, useInfiniteQuery } from '@tanstack/react-query';
import { useInView } from 'react-intersection-observer';
import api from '../services/api';
import HoverableMediaCardWrapper from '../components/items/HoverableMediaCardWrapper';
import { FilterBar } from '../components/library/FilterBar';
import PlaylistsView from '../components/playlists/PlaylistsView';
import { type MediaItem, type PagedResult, type Library } from '../types';
import { useMediaHub } from '../hooks/useMediaHub';
import useSequentialReveal from '../hooks/useSequentialReveal';

export default function LibraryPage() {
    const { ref, inView } = useInView();
    const { id } = useParams<{ id: string }>();
    const [hoveredId, setHoveredId] = useState<string | null>(null);

    // SignalR real-time updates - replaces polling
    useMediaHub({ libraryId: id });

    // Fetch Library Details
    const { data: library } = useQuery({
        queryKey: ['library', id],
        queryFn: async () => {
            const res = await api.get<Library>(`/libraries/${id}`);
            return res.data;
        },
        enabled: !!id
    });


    // Filter State
    const [search, setSearch] = useState('');
    const [sortBy, setSortBy] = useState('title');
    // Null = the sort key's natural direction, resolved server-side (SortDirection).
    const [sortDir, setSortDir] = useState<'asc' | 'desc' | null>(null);
    const [genre, setGenre] = useState('');
    const [year, setYear] = useState<number | null>(null);
    const [minRating, setMinRating] = useState<number | null>(null);
    const [isFavorite, setIsFavorite] = useState<boolean | null>(null);
    const [watched, setWatched] = useState<boolean | null>(null);
    const [viewMode, setViewMode] = useState('artists'); // Default to artists for Music

    // Playlists are user-owned (not part of the library item set), so the
    // library-items query is skipped when the Playlists tab is active. The
    // PlaylistsView component renders its own data via React Query.
    const isPlaylistsView = viewMode === 'playlists' && library?.type === 'Music';

    const {
        data,
        isLoading,
        error,
        fetchNextPage,
        hasNextPage,
        isFetchingNextPage
    } = useInfiniteQuery({
        queryKey: ['library', id, 'items', { search, sortBy, sortDir, genre, year, minRating, isFavorite, watched, viewMode }],
        queryFn: async ({ pageParam = 1 }) => {
            const params: Record<string, string | number | boolean | undefined | null> = {
                page: pageParam,
                pageSize: 50,
                search,
                sortBy,
                sortDir,
                genre,
                year,
                minRating,
                isFavorite,
                watched,
                // Always send viewMode - backend will ignore for non-music libraries
                viewMode: viewMode
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
        enabled: !!id && !isPlaylistsView,
    });

    useEffect(() => {
        if (inView && hasNextPage) {
            fetchNextPage();
        }
    }, [inView, hasNextPage, fetchNextPage]);

    const allItems = data?.pages.flatMap((page: PagedResult<MediaItem>) => page.items) || [];

    // Sequential left-to-right cascade reveal.
    // Count growth from infinite-scroll does NOT reset the cascade — new items
    // cascade in from where the cursor stopped.
    const reveal = useSequentialReveal(allItems.length);

    // Explicitly reset the cascade when the result set changes (filter/sort/nav).
    useEffect(() => {
        reveal.reset();
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [id, search, sortBy, sortDir, genre, year, minRating, isFavorite, watched, viewMode]);

    // Render content area based on state
    const renderContent = () => {
        // Music library "Playlists" view-mode tab — playlists are user-owned
        // data unrelated to the library's media items, so we render a
        // dedicated grid here instead of the standard card layout.
        if (isPlaylistsView) {
            return <PlaylistsView />;
        }

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
            <div className="flex-1 px-8 pt-8 pb-10">
                {/* CSS Grid for fixed positions - prevents reflow on hover */}
                <div
                    className="grid"
                    style={{
                        gridTemplateColumns: 'repeat(auto-fill, 192px)',
                        gap: '2rem' // gap-8
                    }}
                >
                    {allItems.map((item: MediaItem, i: number) => (
                        <HoverableMediaCardWrapper
                            key={item.id}
                            item={item}
                            hoveredId={hoveredId}
                            setHoveredId={setHoveredId}
                            libraryType={library?.type}
                            groupReady={reveal.isRevealed(i)}
                            onImageLoad={() => reveal.onImageLoad(i)}
                            onImageError={() => reveal.onImageError(i)}
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

    // Rescan handler
    const handleRescan = async () => {
        try {
            await api.post(`/libraries/${id}/scan`);
            // Toast will show automatically via SignalR -> Store -> Toast
        } catch (error) {
            console.error('Failed to start scan:', error);
        }
    };

    return (
        <div className="min-h-screen bg-background flex flex-col">
            {/* FilterBar - ALWAYS rendered to preserve state */}
            <FilterBar
                onSearch={setSearch}
                onSort={setSortBy}
                onSortDir={setSortDir}
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
                onRescan={handleRescan}
            />

            {/* Content area - rendered conditionally based on loading/error state */}
            {renderContent()}
        </div>
    );
}

