import { Search, Heart } from 'lucide-react';
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
    libraryId
}: FilterBarProps) {
    const [search, setSearch] = useState('');
    const [genre, setGenre] = useState('');
    const [year, setYear] = useState('');
    const [rating, setRating] = useState('');
    const [isFavorite, setIsFavorite] = useState<boolean | null>(null);
    const [watched, setWatched] = useState<string>('');
    const [sort, setSort] = useState('title');

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
        ];
    }, [isMusicLibrary, isPhotoLibrary]);

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
                                { id: 'songs', label: 'Tracks' }
                            ].map((option) => (
                                <button
                                    key={option.id}
                                    onClick={() => onViewModeChange(option.id)}
                                    className={cn(
                                        "px-3 py-1 text-sm font-medium rounded-md transition-all",
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
                            setSort(e.target.value);
                            onSort(e.target.value);
                        }}
                        className={selectStyles}
                    >
                        {getSortOptions().map((option) => (
                            <option key={option.value} value={option.value} className={optionStyles}>
                                {option.label}
                            </option>
                        ))}
                    </select>

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
                </div>
            </div>
        </div>
    );
}



