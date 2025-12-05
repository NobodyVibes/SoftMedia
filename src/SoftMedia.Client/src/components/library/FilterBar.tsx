import { Search, Heart } from 'lucide-react';
import { useState, useEffect } from 'react';
import { useDebounce } from '../../hooks/useDebounce'; // Assuming this exists or I'll implement it
import { cn } from '../../lib/utils';

interface FilterBarProps {
    onSearch: (query: string) => void;
    onSort: (sort: string) => void;
    onGenre: (genre: string) => void;
    onYear: (year: number | null) => void;
    onRating: (rating: number | null) => void;
    onFavorite: (isFavorite: boolean | null) => void;
    viewMode?: string;
    onViewModeChange?: (mode: string) => void;
}

export function FilterBar({ onSearch, onSort, onGenre, onYear, onRating, onFavorite, viewMode, onViewModeChange }: FilterBarProps) {
    const [search, setSearch] = useState('');
    const [genre, setGenre] = useState('');
    const [year, setYear] = useState('');
    const [rating, setRating] = useState('');
    const [isFavorite, setIsFavorite] = useState<boolean | null>(null);

    const debouncedSearch = useDebounce(search, 500);
    const debouncedGenre = useDebounce(genre, 500);
    const debouncedYear = useDebounce(year, 500);

    useEffect(() => {
        onSearch(debouncedSearch);
    }, [debouncedSearch, onSearch]);

    useEffect(() => {
        onGenre(debouncedGenre);
    }, [debouncedGenre, onGenre]);

    useEffect(() => {
        const y = parseInt(debouncedYear);
        onYear(isNaN(y) ? null : y);
    }, [debouncedYear, onYear]);

    return (
        <div className="bg-black/20 border-b border-white/10 p-4 sticky top-16 z-20 backdrop-blur-md">
            <div className="container mx-auto flex flex-col md:flex-row gap-4 items-center justify-between">

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

                    {/* View Mode Toggle (Only if provided) */}
                    {onViewModeChange && (
                        <select
                            value={viewMode || 'albums'}
                            onChange={(e) => onViewModeChange(e.target.value)}
                            className="bg-white/5 border border-white/10 rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-primary/50"
                        >
                            <option value="albums">Albums</option>
                            <option value="artists">Artists</option>
                            <option value="songs">Songs</option>
                        </select>
                    )}

                    {/* Genre */}
                    <div className="relative w-32">
                        <input
                            type="text"
                            value={genre}
                            onChange={(e) => setGenre(e.target.value)}
                            placeholder="Genre"
                            className="w-full bg-white/5 border border-white/10 rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-primary/50"
                        />
                    </div>

                    {/* Year */}
                    <div className="relative w-24">
                        <input
                            type="number"
                            value={year}
                            onChange={(e) => setYear(e.target.value)}
                            placeholder="Year"
                            className="w-full bg-white/5 border border-white/10 rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-primary/50"
                        />
                    </div>

                    {/* Rating */}
                    <select
                        value={rating}
                        onChange={(e) => {
                            setRating(e.target.value);
                            onRating(e.target.value ? parseInt(e.target.value) : null);
                        }}
                        className="bg-white/5 border border-white/10 rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-primary/50"
                    >
                        <option value="">Rating</option>
                        <option value="1">1+ Stars</option>
                        <option value="2">2+ Stars</option>
                        <option value="3">3+ Stars</option>
                        <option value="4">4+ Stars</option>
                        <option value="5">5 Stars</option>
                    </select>

                    {/* Sort */}
                    <select
                        onChange={(e) => onSort(e.target.value)}
                        className="bg-white/5 border border-white/10 rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-primary/50"
                    >
                        <option value="title">Title</option>
                        <option value="dateadded">Date Added</option>
                        <option value="year">Year</option>
                        <option value="rating">Rating</option>
                    </select>

                    {/* Favorite Toggle */}
                    <button
                        onClick={() => {
                            const newVal = isFavorite === true ? null : true; // Toggle between True and Null (All)
                            // Or maybe True -> False -> Null?
                            // Let's do: Null (All) -> True (Favs) -> False (Non-Favs) -> Null
                            // Or just Toggle Favs vs All.
                            // Requirement says "Favorites toggle works".
                            // Let's do simple toggle: Show Favorites Only vs Show All.
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
