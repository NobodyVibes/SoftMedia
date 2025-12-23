import { useState, useEffect } from 'react';
import { useParams } from 'react-router-dom';
import { useQuery, useInfiniteQuery } from '@tanstack/react-query';
import { useInView } from 'react-intersection-observer';
import api from '../services/api';
import HoverableMediaCardWrapper from '../components/items/HoverableMediaCardWrapper';
import { FilterBar } from '../components/library/FilterBar';
import { type MediaItem, type PagedResult, type Library } from '../types';

export default function LibraryPage() {
    const { ref, inView } = useInView();
    const { id } = useParams<{ id: string }>();
    const [hoveredId, setHoveredId] = useState<string | null>(null);

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

    if (isLoading) return <div className="p-8 text-center text-gray-400">Loading library...</div>;
    if (error) return <div className="p-8 text-center text-red-400">Error loading library.</div>;

    const allItems = data?.pages.flatMap((page: PagedResult<MediaItem>) => page.items) || [];

    return (
        <div className="min-h-screen bg-background">
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
            />

            <div className="container mx-auto px-4 py-8">
                {allItems.length === 0 ? (
                    <div className="text-center text-gray-500 mt-12">
                        <p className="text-xl">No items found.</p>
                        <p className="text-sm">Try adjusting your filters.</p>
                    </div>
                ) : (
                    <>
                        <div className="flex flex-wrap gap-6 justify-center">
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
                    </>
                )}
            </div>
        </div>
    );
}
