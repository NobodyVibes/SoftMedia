import { Search, Heart, RefreshCw, ArrowUpNarrowWide, ArrowDownWideNarrow } from 'lucide-react';
import { useState, useEffect, useCallback } from 'react';
import { useDebounce } from '../../hooks/useDebounce';
import { cn } from '../../lib/utils';
import { GenreComboBox } from './GenreComboBox';

interface FilterBarProps {
    onSearch: (query: string) => void;
    onSort: (sort: string) => void;
    onGenre: (genre: string) => void;
    onYear: (year: number | null) => void;
    onRating: (rating: number | null) => void;
    onFavorite: (isFavorite: boolean | null) => void;
    onWatched?: (watched: boolean | null) => void;
    viewMode?: string;
    onViewModeChange?: (mode: string) => void;
    showWatchedFilter?: boolean;
    libraryType?: string;
    libraryId?: string;
    onRescan?: () => void;
    /**
     * Starting values for the controls. The browse page seeds these from the URL so a
     * row's "See more" opens with that row's criteria already selected — arriving at a
     * Comedy grid with the genre box blank makes the filters look broken and silently
     * discards them the moment anything else is touched.
     *
     * Uncontrolled after mount: these initialise state, they do not track it. The page
     * owns the values from then on.
     */
    initialValues?: {
        search?: string;
        genre?: string;
        year?: string;
        rating?: string;
        isFavorite?: boolean | null;
        watched?: boolean | null;
        sort?: string;
        sortDir?: 'asc' | 'desc';
    };
    /** Extra sort options beyond the type-derived defaults, e.g. the browse page's play-count sorts. */
    extraSortOptions?: Array<{ value: string; label: string }>;
    /** Media types to scope genre suggestions to when there is no libraryId. */
    genreTypes?: string;
    /**
     * Receives 'asc' | 'desc'. Omit to hide the direction toggle entirely — callers
     * whose backend ignores the parameter should not show a control that does nothing.
     */
    onSortDir?: (dir: 'asc' | 'desc') => void;
}

/**
 * The direction a sort key means when the user hasn't said otherwise: titles read A-Z,
 * everything else (dates, years, ratings, play counts) means newest/highest/most first.
 *
 * MUST mirror SortDirection.NaturalFor on the server. If the two disagree, the arrow
 * icon claims one direction while the query runs the other.
 */
const ASCENDING_BY_NATURE = new Set(['title', 'artist']);

export function naturalDirectionFor(sortKey: string): 'asc' | 'desc' {
    return ASCENDING_BY_NATURE.has(sortKey) ? 'asc' : 'desc';
}

// Common styles for select elements
const selectStyles = "bg-[#2a2a2a] border border-white/10 rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-primary/50 cursor-pointer";
const optionStyles = "bg-[#2a2a2a] text-white";

export function FilterBar({
    onSearch,
    onSort,
    onGenre,
    onYear,
    onRating,
    onFavorite,
    onWatched,
    viewMode,
    onViewModeChange,
    showWatchedFilter,
    libraryType,
    libraryId,
    onRescan,
    initialValues,
    extraSortOptions,
    genreTypes,
    onSortDir
}: FilterBarProps) {
    const [search, setSearch] = useState(initialValues?.search ?? '');
    const [genre, setGenre] = useState(initialValues?.genre ?? '');
    const [year, setYear] = useState(initialValues?.year ?? '');
    const [rating, setRating] = useState(initialValues?.rating ?? '');
    const [isFavorite, setIsFavorite] = useState<boolean | null>(initialValues?.isFavorite ?? null);
    const [watched, setWatched] = useState<string>(
        initialValues?.watched === true ? 'watched'
            : initialValues?.watched === false ? 'unwatched'
                : '');
    const [sort, setSort] = useState(initialValues?.sort ?? 'title');
    const [sortDir, setSortDir] = useState<'asc' | 'desc'>(
        initialValues?.sortDir ?? naturalDirectionFor(initialValues?.sort ?? 'title'));

    const debouncedSearch = useDebounce(search, 500);
    const debouncedGenre = useDebounce(genre, 500);
    const debouncedYear = useDebounce(year, 500);

    // Use refs to store callbacks to avoid dependency issues
    // eslint-disable-next-line react-hooks/exhaustive-deps
    useEffect(() => {
        onSearch(debouncedSearch);
    }, [debouncedSearch]); // Intentionally omitting onSearch - it's a stable setState dispatcher

    // eslint-disable-next-line react-hooks/exhaustive-deps
    useEffect(() => {
        onGenre(debouncedGenre);
    }, [debouncedGenre, onGenre]); // Include onGenre since we need it for GenreComboBox

    // eslint-disable-next-line react-hooks/exhaustive-deps
    useEffect(() => {
        const y = parseInt(debouncedYear);
        onYear(isNaN(y) ? null : y);
    }, [debouncedYear]); // Intentionally omitting onYear - it's a stable setState dispatcher

    // Determine which filters to show based on library type
    const isMusicLibrary = libraryType === 'Music';
    const isPhotoLibrary = libraryType === 'Photo';

    // Get sort options based on library type
    const getSortOptions = useCallback(() => {
        const common = [
            { value: 'title', label: 'Title' },
            { value: 'dateadded', label: 'Date Added' },
            { value: 'rating', label: 'Rating' },
        ];

        if (isMusicLibrary) {
            return [
                ...common,
                { value: 'artist', label: 'Artist' },
            ];
        }

        if (isPhotoLibrary) {
            return [
                { value: 'dateadded', label: 'Date Added' },
                { value: 'title', label: 'Title' },
            ];
        }

        return [
            ...common,
            { value: 'year', label: 'Year' },
            // Play-history aggregates (R-WI-013): plays land on movies/episodes, so
            // these rank Movie grids directly and TV grids via the series rollup the
            // server performs. Book/music/photo grids have no play data → omitted.
            ...(libraryType === 'Movie' || libraryType === 'TV'
                ? [
                    { value: 'playcount', label: 'Most Played' },
                    { value: 'lastplayed', label: 'Recently Played' },
                ]
                : []),
            // Caller-supplied extras (the browse page adds its personal play-count
            // sort), de-duplicated so an extra can't double up with one above.
            ...(extraSortOptions ?? []),
        ].filter((opt, i, all) => all.findIndex(o => o.value === opt.value) === i);
    }, [isMusicLibrary, isPhotoLibrary, libraryType, extraSortOptions]);

    return (
        <div className="sticky top-0 z-30 bg-black/40 border-b border-white/10 px-8 py-4 backdrop-blur-md">
            <div className="flex flex-col md:flex-row gap-4 items-center justify-between">

                {/* Search */}
                <div className="relative w-full md:w-64">
                    <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-gray-400" />
                    <input
                        type="text"
                        value={search}
                        onChange={(e) => setSearch(e.target.value)}
                        placeholder="Search..."
                        className="w-full bg-white/5 border border-white/10 rounded-full pl-10 pr-4 py-2 text-sm text-white focus:outline-none focus:border-primary/50 transition-colors"
                    />
                </div>

                {/* Filters */}
                <div className="flex flex-wrap items-center gap-3 w-full md:w-auto">

                    {/* View Mode Toggle (Only for Music libraries) */}
                    {onViewModeChange && (
                        <div className="flex items-center bg-white/5 border border-white/10 rounded-lg p-1 h-[38px]">
                            {[
                                { id: 'albums', label: 'Albums' },
                                { id: 'artists', label: 'Artists' },
                                { id: 'songs', label: 'Tracks' },
                                // Playlists live inside the Music library as a
                                // view-mode tab, not a global sidebar entry
                                // (they're music-only in v1).
                                { id: 'playlists', label: 'Playlists' }
                            ].map((option) => (
                                <button
                                    key={option.id}
                                    type="button"
                                    onClick={() => onViewModeChange(option.id)}
                                    className={cn(
                                        "px-3 py-1 text-sm font-medium rounded-md transition-all focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400",
                                        (viewMode || 'artists') === option.id
                                            ? "bg-primary text-white shadow-sm"
                                            : "text-gray-400 hover:text-white hover:bg-white/5"
                                    )}
                                >
                                    {option.label}
                                </button>
                            ))}
                        </div>
                    )}

                    {/* Genre Filter - Combo box with autocomplete */}
                    <GenreComboBox
                        libraryId={libraryId}
                        types={genreTypes}
                        value={genre}
                        onChange={setGenre}
                    />

                    {/* Year Filter - always visible */}
                    <div className="relative w-24">
                        <input
                            type="number"
                            value={year}
                            onChange={(e) => setYear(e.target.value)}
                            placeholder="Year"
                            min="1900"
                            max={new Date().getFullYear() + 1}
                            className="w-full bg-white/5 border border-white/10 rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-primary/50"
                        />
                    </div>

                    {/* Rating Filter - always visible (10-star system) */}
                    <select
                        value={rating}
                        onChange={(e) => {
                            setRating(e.target.value);
                            onRating(e.target.value ? parseInt(e.target.value) : null);
                        }}
                        className={selectStyles}
                    >
                        <option value="" className={optionStyles}>All Ratings</option>
                        <option value="1" className={optionStyles}>1+ Stars</option>
                        <option value="2" className={optionStyles}>2+ Stars</option>
                        <option value="3" className={optionStyles}>3+ Stars</option>
                        <option value="4" className={optionStyles}>4+ Stars</option>
                        <option value="5" className={optionStyles}>5+ Stars</option>
                        <option value="6" className={optionStyles}>6+ Stars</option>
                        <option value="7" className={optionStyles}>7+ Stars</option>
                        <option value="8" className={optionStyles}>8+ Stars</option>
                        <option value="9" className={optionStyles}>9+ Stars</option>
                        <option value="10" className={optionStyles}>10 Stars</option>
                    </select>

                    {/* Watched Filter (for TV/Movie libraries) */}
                    {showWatchedFilter && onWatched && (
                        <select
                            value={watched}
                            onChange={(e) => {
                                setWatched(e.target.value);
                                if (e.target.value === 'watched') {
                                    onWatched(true);
                                } else if (e.target.value === 'unwatched') {
                                    onWatched(false);
                                } else {
                                    onWatched(null);
                                }
                            }}
                            className={selectStyles}
                        >
                            <option value="" className={optionStyles}>All Status</option>
                            <option value="watched" className={optionStyles}>Watched</option>
                            <option value="unwatched" className={optionStyles}>Unwatched</option>
                        </select>
                    )}

                    {/* Sort Dropdown */}
                    <select
                        value={sort}
                        onChange={(e) => {
                            const nextSort = e.target.value;
                            setSort(nextSort);
                            onSort(nextSort);
                            // Reset to the new key's natural direction. Carrying the old
                            // one over means switching from Title (A-Z) to Date Added
                            // lands on OLDEST first, which reads as broken.
                            if (onSortDir) {
                                const natural = naturalDirectionFor(nextSort);
                                setSortDir(natural);
                                onSortDir(natural);
                            }
                        }}
                        className={selectStyles}
                    >
                        {getSortOptions().map((option) => (
                            <option key={option.value} value={option.value} className={optionStyles}>
                                {option.label}
                            </option>
                        ))}
                    </select>

                    {/* Sort Direction */}
                    {onSortDir && (
                        <button
                            type="button"
                            onClick={() => {
                                const next = sortDir === 'asc' ? 'desc' : 'asc';
                                setSortDir(next);
                                onSortDir(next);
                            }}
                            className="p-2 rounded-lg border bg-white/5 border-white/10 text-gray-400 hover:text-white hover:bg-white/10 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                            // Names the RESULT, not the icon — "Sort ascending" on a
                            // control that is already ascending is ambiguous to a screen
                            // reader user.
                            aria-label={sortDir === 'asc' ? 'Sort descending' : 'Sort ascending'}
                            title={sortDir === 'asc' ? 'Ascending — click for descending' : 'Descending — click for ascending'}
                        >
                            {sortDir === 'asc'
                                ? <ArrowUpNarrowWide className="w-5 h-5" />
                                : <ArrowDownWideNarrow className="w-5 h-5" />}
                        </button>
                    )}

                    {/* Favorite Toggle */}
                    <button
                        onClick={() => {
                            const newVal = isFavorite === true ? null : true;
                            setIsFavorite(newVal);
                            onFavorite(newVal);
                        }}
                        className={cn(
                            "p-2 rounded-lg border transition-colors",
                            isFavorite
                                ? "bg-red-500/20 border-red-500/50 text-red-500"
                                : "bg-white/5 border-white/10 text-gray-400 hover:text-white"
                        )}
                        title="Show Favorites Only"
                    >
                        <Heart className={cn("w-5 h-5", isFavorite && "fill-current")} />
                    </button>

                    {/* Rescan Button */}
                    {onRescan && (
                        <button
                            onClick={onRescan}
                            className="p-2 rounded-lg border bg-white/5 border-white/10 text-gray-400 hover:text-white hover:bg-white/10 transition-colors"
                            title="Rescan Library"
                        >
                            <RefreshCw className="w-5 h-5" />
                        </button>
                    )}
                </div>
            </div>
        </div>
    );
}



