import { useState, useRef, useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import { ChevronDown, X } from 'lucide-react';
import api from '../../services/api';
import { cn } from '../../lib/utils';

interface GenreComboBoxProps {
    libraryId?: string;
    value: string;
    onChange: (value: string) => void;
}

export function GenreComboBox({ libraryId, value, onChange }: GenreComboBoxProps) {
    const [isOpen, setIsOpen] = useState(false);
    const [inputValue, setInputValue] = useState(value);
    const containerRef = useRef<HTMLDivElement>(null);
    const inputRef = useRef<HTMLInputElement>(null);

    // Fetch genres for this library
    const { data: genres = [] } = useQuery<string[]>({
        queryKey: ['library', libraryId, 'genres'],
        queryFn: async () => {
            if (!libraryId) return [];
            const res = await api.get<string[]>(`/libraries/${libraryId}/genres`);
            return res.data;
        },
        enabled: !!libraryId,
        staleTime: 5 * 60 * 1000, // Cache for 5 minutes
    });

    // Filter genres based on input
    const filteredGenres = genres.filter(g =>
        g.toLowerCase().includes(inputValue.toLowerCase())
    );

    // Sync inputValue with external value prop
    useEffect(() => {
        setInputValue(value);
    }, [value]);

    // Close dropdown when clicking outside
    useEffect(() => {
        const handleClickOutside = (e: MouseEvent) => {
            if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
                setIsOpen(false);
            }
        };
        document.addEventListener('mousedown', handleClickOutside);
        return () => document.removeEventListener('mousedown', handleClickOutside);
    }, []);

    const handleSelect = (genre: string) => {
        setInputValue(genre);
        onChange(genre);
        setIsOpen(false);
    };

    const handleClear = () => {
        setInputValue('');
        onChange('');
        inputRef.current?.focus();
    };

    const handleInputChange = (e: React.ChangeEvent<HTMLInputElement>) => {
        const newValue = e.target.value;
        setInputValue(newValue);
        onChange(newValue);
        if (!isOpen) setIsOpen(true);
    };

    return (
        <div ref={containerRef} className="relative w-36">
            <div className="relative">
                <input
                    ref={inputRef}
                    type="text"
                    value={inputValue}
                    onChange={handleInputChange}
                    onFocus={() => setIsOpen(true)}
                    placeholder="Genre"
                    className="w-full bg-white/5 border border-white/10 rounded-lg pl-3 pr-8 py-2 text-sm text-white focus:outline-none focus:border-primary/50"
                />
                <div className="absolute right-1 top-1/2 -translate-y-1/2 flex items-center gap-0.5">
                    {inputValue && (
                        <button
                            onClick={handleClear}
                            className="p-1 text-gray-400 hover:text-white"
                            type="button"
                        >
                            <X className="w-3 h-3" />
                        </button>
                    )}
                    <button
                        onClick={() => setIsOpen(!isOpen)}
                        className="p-1 text-gray-400 hover:text-white"
                        type="button"
                    >
                        <ChevronDown className={cn("w-4 h-4 transition-transform", isOpen && "rotate-180")} />
                    </button>
                </div>
            </div>

            {/* Dropdown */}
            {isOpen && (
                <div className="absolute top-full left-0 right-0 mt-1 max-h-60 overflow-y-auto bg-[#2a2a2a] border border-white/10 rounded-lg shadow-lg z-50">
                    {filteredGenres.length === 0 ? (
                        <div className="px-3 py-2 text-sm text-gray-400">
                            {genres.length === 0 ? 'No genres available' : 'No matches'}
                        </div>
                    ) : (
                        filteredGenres.map((genre) => (
                            <button
                                key={genre}
                                onClick={() => handleSelect(genre)}
                                className={cn(
                                    "w-full text-left px-3 py-2 text-sm hover:bg-white/10 transition-colors",
                                    inputValue === genre ? "text-primary bg-white/5" : "text-white"
                                )}
                                type="button"
                            >
                                {genre}
                            </button>
                        ))
                    )}
                </div>
            )}
        </div>
    );
}
