import { useState } from 'react';
import { ArrowRight } from 'lucide-react';
import { type MediaItem } from '../../types';
import HoverableMediaCardWrapper from '../items/HoverableMediaCardWrapper';
import HorizontalScrollList from './HorizontalScrollList';

interface MediaRowProps {
    title: string;
    items: MediaItem[];
    viewAllLink?: string;
    libraryType?: string;
}

export default function MediaRow({ title, items, viewAllLink, libraryType }: MediaRowProps) {
    const [hoveredId, setHoveredId] = useState<string | null>(null);

    if (!items || items.length === 0) return null;

    return (
        <div className="mb-12 group/row relative z-10 overflow-x-hidden">
            {/* Section Header */}
            <div className="flex items-center justify-between mb-4 px-6">
                <h2 className="text-2xl font-bold text-white">
                    {title}
                </h2>
                {viewAllLink && (
                    <a
                        href={viewAllLink}
                        className="flex items-center gap-1 text-sm text-gray-400 hover:text-white transition-colors group"
                    >
                        <span>VIEW ALL</span>
                        <ArrowRight size={16} className="group-hover:translate-x-1 transition-transform" />
                    </a>
                )}
            </div>

            <HorizontalScrollList
                className="py-24 px-24 -mx-18 items-center"
                gap="gap-8"
            >
                {items.map((item) => (
                    <div key={item.id} className="flex-shrink-0" style={{ width: '180px' }}>
                        <HoverableMediaCardWrapper
                            item={item}
                            hoveredId={hoveredId}
                            setHoveredId={setHoveredId}
                            libraryType={libraryType}
                            width="100%"
                        />
                    </div>
                ))}
            </HorizontalScrollList>
        </div>
    );
}

