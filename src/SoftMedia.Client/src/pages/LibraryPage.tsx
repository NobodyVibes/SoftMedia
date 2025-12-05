import { useState } from 'react';
import { useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import api from '../services/api';
import HoverableMediaCardWrapper from '../components/items/HoverableMediaCardWrapper';
import { FilterBar } from '../components/library/FilterBar';
import { type MediaItem, type PagedResult, type Library } from '../types';

export default function LibraryPage() {
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
    const [viewMode, setViewMode] = useState('albums'); // Default to albums for Music

    const { data, isLoading, error } = useQuery({
        queryKey: ['library', id, 'items', { search, sortBy, genre, year, minRating, isFavorite, viewMode }],
        queryFn: async () => {
            const params: any = {
                page: 1,
                pageSize: 100, // Fetch more for now
                search,
                sortBy,
                genre,
                year,
                minRating,
                isFavorite,
                viewMode: library?.type === 'Music' ? viewMode : undefined
            };
            // Clean undefined/null params
            Object.keys(params).forEach(key => (params[key] === null || params[key] === '') && delete params[key]);

            const response = await api.get<PagedResult<MediaItem>>(`/libraries/${id}/items`, { params });
            return response.data;
        },
        enabled: !!id
    });

    if (isLoading) return <div className="p-8 text-center text-gray-400">Loading library...</div>;
    if (error) return <div className="p-8 text-center text-red-400">Error loading library.</div>;

    return (
        <div className="min-h-screen bg-background">
            <FilterBar
                onSearch={setSearch}
                onSort={setSortBy}
                onGenre={setGenre}
                onYear={setYear}
                onRating={setMinRating}
                onFavorite={setIsFavorite}
                viewMode={library?.type === 'Music' ? viewMode : undefined}
                onViewModeChange={library?.type === 'Music' ? setViewMode : undefined}
            />

            <div className="container mx-auto px-4 py-8">
                {data?.items.length === 0 ? (
                    <div className="text-center text-gray-500 mt-12">
                        <p className="text-xl">No items found.</p>
                        <p className="text-sm">Try adjusting your filters.</p>
                    </div>
                ) : (
                    <div className="flex flex-wrap gap-6 justify-center">
                        {data?.items.map((item) => (
                            <HoverableMediaCardWrapper
                                key={item.id}
                                item={item}
                                hoveredId={hoveredId}
                                setHoveredId={setHoveredId}
                            />
                        ))}
                    </div>
                )}
            </div>
        </div>
    );
}
