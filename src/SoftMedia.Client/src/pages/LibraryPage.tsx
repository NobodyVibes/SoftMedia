import { useState } from 'react';
import { useParams } from 'react-router-dom';
import { useLibrary, useLibraryItems } from '../hooks/useLibrary';
import FilterBar from '../components/library/FilterBar';
import LibraryGrid from '../components/library/LibraryGrid';

export default function LibraryPage() {
    const { id } = useParams<{ id: string }>();
    const [search, setSearch] = useState('');
    const [sortBy, setSortBy] = useState('dateAdded_desc');

    const { data: library } = useLibrary(id!);
    const {
        data,
        fetchNextPage,
        hasNextPage,
        isLoading
    } = useLibraryItems(id!, search, sortBy);

    const items = data?.pages.flatMap((page) => page.items) || [];

    return (
        <div className="container mx-auto px-4 py-8">
            <div className="mb-8">
                <h1 className="text-3xl font-bold text-white mb-2">
                    {library?.name || 'Library'}
                </h1>
                <p className="text-gray-400">
                    {items.length} items
                </p>
            </div>

            <FilterBar
                search={search}
                onSearchChange={setSearch}
                sortBy={sortBy}
                onSortChange={setSortBy}
            />

            <LibraryGrid
                items={items}
                isLoading={isLoading}
                hasNextPage={!!hasNextPage}
                fetchNextPage={fetchNextPage}
                libraryType={library?.type}
            />
        </div>
    );
}
