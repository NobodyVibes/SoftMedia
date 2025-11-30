import { useEffect } from 'react';
import { useInView } from 'react-intersection-observer';
import MediaCard from '../items/MediaCard';
import { type MediaItem } from '../../types';

interface LibraryGridProps {
    items: MediaItem[];
    isLoading: boolean;
    hasNextPage: boolean;
    fetchNextPage: () => void;
    libraryType?: string;
}

export default function LibraryGrid({ items, isLoading, hasNextPage, fetchNextPage, libraryType }: LibraryGridProps) {
    const { ref, inView } = useInView();

    useEffect(() => {
        if (inView && hasNextPage) {
            fetchNextPage();
        }
    }, [inView, hasNextPage, fetchNextPage]);

    if (items.length === 0 && !isLoading) {
        return (
            <div className="text-center py-20 text-gray-500">
                <p>No items found in this library.</p>
            </div>
        );
    }

    return (
        <>
            <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6 gap-6">
                {items.map((item) => (
                    <MediaCard key={item.id} item={item} libraryType={libraryType} />
                ))}

                {isLoading && (
                    // Skeleton Loaders
                    Array.from({ length: 10 }).map((_, i) => (
                        <div key={`skeleton-${i}`} className="aspect-[2/3] rounded-lg bg-slate-800 animate-pulse" />
                    ))
                )}
            </div>

            {/* Infinite Scroll Trigger */}
            <div ref={ref} className="h-10 w-full" />
        </>
    );
}
