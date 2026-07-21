import { useCallback, useEffect, useState } from 'react';
import { useSearchParams, Link } from 'react-router-dom';
import { useInfiniteQuery } from '@tanstack/react-query';
import { useInView } from 'react-intersection-observer';
import { ArrowLeft } from 'lucide-react';
import api from '../services/api';
import HoverableMediaCardWrapper from '../components/items/HoverableMediaCardWrapper';
import { FilterBar } from '../components/library/FilterBar';
import useSequentialReveal from '../hooks/useSequentialReveal';
import { useMediaTokenRefresh } from '../hooks/useMediaTokenRefresh';
import type { MediaItem, PagedResult } from '../types';

/**
 * Cross-library browse grid — the destination of every home row's "See more".
 *
 * Behaves like a library page: search, genre, year, rating, watched and sort all work,
 * and the row's own criteria arrive pre-selected in those controls. That pre-selection
 * is the point of the URL-as-state design here — a Comedy row landing on a grid with an
 * empty genre box looks broken, and the criteria would be silently dropped as soon as
 * the user touched anything else.
 *
 * Every control writes back to the query string, so the view stays shareable, survives
 * a refresh, and the browser's Back button steps through filter changes. LibraryPage
 * keeps its filters in local state and cannot do any of that.
 */
const PAGE_SIZE = 50;

/**
 * Sorts unique to this page. "playcount" and "lastplayed" come from FilterBar's own
 * Movie/TV options; this adds the personal counterpart so a "See more" from Most
 * Watched's Me scope keeps ranking by the caller's own plays.
 */
const EXTRA_SORTS = [
    { value: 'playcount', label: 'Most Played (Everyone)' },
    { value: 'myplaycount', label: 'Most Played (You)' },
    { value: 'lastplayed', label: 'Recently Played' },
];

/** Human heading for the active criteria. */
function titleFor(params: URLSearchParams): string {
    const genre = params.get('genre');
    const decade = params.get('decade');

    if (genre && decade) return `${genre} · ${decade}s`;
    if (genre) return genre;
    if (decade) return `From the ${decade}s`;
    if (params.get('unplayed') === 'true') return 'Never Played';
    if (params.get('inProgress') === 'true') return 'Continue Watching';

    const sort = params.get('sortBy');
    if (sort === 'playcount') return 'Most Watched';
    if (sort === 'myplaycount') return 'Your Most Watched';
    if (sort === 'dateadded') return 'Recently Added';
    return 'Browse';
}

export default function BrowsePage() {
    // Media URLs embed the media token; re-render when it rotates so a stale token
    // can't leave the artwork permanently broken.
    useMediaTokenRefresh();

    const [searchParams, setSearchParams] = useSearchParams();
    const [hoveredId, setHoveredId] = useState<string | null>(null);
    const { ref, inView } = useInView();

    const genre = searchParams.get('genre') ?? undefined;
    const decade = searchParams.get('decade') ?? undefined;
    const unplayed = searchParams.get('unplayed') ?? undefined;
    const inProgress = searchParams.get('inProgress') ?? undefined;
    const libraryId = searchParams.get('libraryId') ?? undefined;
    const sortBy = searchParams.get('sortBy') ?? undefined;
    const sortDir = searchParams.get('sortDir') ?? undefined;
    const types = searchParams.get('types') ?? undefined;
    const search = searchParams.get('search') ?? undefined;
    const year = searchParams.get('year') ?? undefined;
    const minRating = searchParams.get('minRating') ?? undefined;
    const isFavorite = searchParams.get('isFavorite') ?? undefined;
    const watched = searchParams.get('watched') ?? undefined;

    /**
     * Write one criterion into the URL. Empty/null clears the key rather than writing a
     * blank value, so the query string stays clean and the server never receives an
     * empty filter it would treat as active.
     *
     * `replace` keeps filter tweaks out of the history stack — otherwise Back would
     * walk character-by-character through a typed search term.
     */
    const setParam = useCallback((key: string, value: string | null) => {
        setSearchParams(prev => {
            const wanted = value === null || value === '' ? null : value;
            // Bail when nothing actually changes. FilterBar fires each of its
            // callbacks once on mount with the seeded value, which would otherwise
            // navigate straight back to the URL we are already on.
            if ((prev.get(key) ?? null) === wanted) return prev;

            const next = new URLSearchParams(prev);
            if (wanted === null) next.delete(key);
            else next.set(key, wanted);
            return next;
        }, { replace: true });
    }, [setSearchParams]);

    // Stable identities are REQUIRED, not a micro-optimisation. FilterBar lists
    // `onGenre` in an effect's dependency array, so an inline arrow — a new function
    // every render — retriggers that effect on every render. Each run writes to the
    // URL, which re-renders this component, which creates another new arrow: an
    // infinite loop that hangs the page (and killed the test worker outright).
    const handleSearch = useCallback((q: string) => setParam('search', q || null), [setParam]);
    const handleSort = useCallback((s: string) => setParam('sortBy', s), [setParam]);
    const handleSortDir = useCallback((d: 'asc' | 'desc') => setParam('sortDir', d), [setParam]);
    const handleGenre = useCallback((g: string) => setParam('genre', g || null), [setParam]);
    const handleYear = useCallback(
        (y: number | null) => setParam('year', y === null ? null : String(y)), [setParam]);
    const handleRating = useCallback(
        (r: number | null) => setParam('minRating', r === null ? null : String(r)), [setParam]);
    const handleFavorite = useCallback(
        (f: boolean | null) => setParam('isFavorite', f === null ? null : String(f)), [setParam]);
    const handleWatched = useCallback(
        (w: boolean | null) => setParam('watched', w === null ? null : String(w)), [setParam]);

    const { data, isLoading, error, fetchNextPage, hasNextPage, isFetchingNextPage } = useInfiniteQuery({
        queryKey: ['browse', {
            genre, decade, unplayed, inProgress, libraryId, sortBy, sortDir, types,
            search, year, minRating, isFavorite, watched,
        }],
        queryFn: async ({ pageParam = 1 }) => {
            const params: Record<string, string | number | undefined> = {
                page: pageParam,
                pageSize: PAGE_SIZE,
                genre, decade, unplayed, inProgress, libraryId, sortBy, sortDir, types,
                search, year, minRating, isFavorite, watched,
            };
            Object.keys(params).forEach(k => params[k] === undefined && delete params[k]);
            const response = await api.get<PagedResult<MediaItem>>('/browse', { params });
            return response.data;
        },
        // Stop on a short page rather than on totalCount — matches how LibraryPage
        // decides, so both grids behave identically.
        getNextPageParam: (lastPage: PagedResult<MediaItem>) =>
            lastPage.items.length < lastPage.pageSize ? undefined : lastPage.page + 1,
        initialPageParam: 1,
    });

    useEffect(() => {
        if (inView && hasNextPage) fetchNextPage();
    }, [inView, hasNextPage, fetchNextPage]);

    const allItems = data?.pages.flatMap(page => page.items) ?? [];
    const total = data?.pages[0]?.totalCount ?? 0;
    const reveal = useSequentialReveal(allItems.length);

    // Restart the cascade when the criteria change — otherwise a new result set
    // inherits the previous run's reveal cursor and pops in without animating.
    useEffect(() => {
        reveal.reset();
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [genre, decade, unplayed, inProgress, libraryId, sortBy, sortDir, types, search, year, minRating, isFavorite, watched]);

    return (
        <div className="min-h-screen">
            <FilterBar
                // Seed every control from the URL so the row's criteria show as selected.
                initialValues={{
                    search: search ?? '',
                    genre: genre ?? '',
                    year: year ?? '',
                    rating: minRating ?? '',
                    isFavorite: isFavorite === 'true' ? true : null,
                    watched: watched === 'true' ? true : watched === 'false' ? false : null,
                    sort: sortBy ?? 'title',
                    sortDir: sortDir === 'asc' || sortDir === 'desc' ? sortDir : undefined,
                }}
                extraSortOptions={EXTRA_SORTS}
                genreTypes={types}
                showWatchedFilter
                onSearch={handleSearch}
                onSort={handleSort}
                onSortDir={handleSortDir}
                onGenre={handleGenre}
                onYear={handleYear}
                onRating={handleRating}
                onFavorite={handleFavorite}
                onWatched={handleWatched}
            />

            <div className="px-8 pt-8">
                <Link
                    to="/"
                    className="inline-flex items-center gap-2 text-sm text-gray-400 hover:text-white focus-visible:text-white focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded mb-6 px-2 py-1"
                >
                    <ArrowLeft className="w-4 h-4" />
                    Home
                </Link>

                <div className="flex items-baseline gap-3 flex-wrap mb-2">
                    <h1 className="text-3xl font-bold text-white tracking-tight">{titleFor(searchParams)}</h1>
                    {!isLoading && !error && (
                        <span className="text-sm text-gray-500">
                            {total} {total === 1 ? 'item' : 'items'}
                        </span>
                    )}
                </div>
            </div>

            {isLoading && (
                <div className="flex items-center justify-center py-24 text-gray-400">
                    <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-primary" />
                </div>
            )}

            {error && (
                <div className="text-center text-red-400 py-24">Error loading results.</div>
            )}

            {!isLoading && !error && allItems.length === 0 && (
                <div className="text-center text-gray-500 py-24">
                    <p className="text-xl">Nothing here.</p>
                    <p className="text-sm">No items match these filters.</p>
                </div>
            )}

            {allItems.length > 0 && (
                <div className="px-8 pt-6 pb-10">
                    {/* Fixed-position grid — prevents reflow on hover, same as LibraryPage. */}
                    <div
                        className="grid"
                        style={{ gridTemplateColumns: 'repeat(auto-fill, 192px)', gap: '2rem' }}
                    >
                        {allItems.map((item, i) => (
                            <HoverableMediaCardWrapper
                                key={item.id}
                                item={item}
                                hoveredId={hoveredId}
                                setHoveredId={setHoveredId}
                                groupReady={reveal.isRevealed(i)}
                                onImageLoad={() => reveal.onImageLoad(i)}
                                onImageError={() => reveal.onImageError(i)}
                            />
                        ))}
                    </div>

                    <div ref={ref} className="h-20 flex justify-center items-center mt-8">
                        {isFetchingNextPage && (
                            <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-primary" />
                        )}
                    </div>
                </div>
            )}
        </div>
    );
}
