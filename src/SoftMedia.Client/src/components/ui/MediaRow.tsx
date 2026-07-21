import { useState, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { ArrowRight } from 'lucide-react';
import { type MediaItem } from '../../types';
import HoverableMediaCardWrapper from '../items/HoverableMediaCardWrapper';
import HorizontalScrollList from './HorizontalScrollList';
import useSequentialReveal from '../../hooks/useSequentialReveal';

interface MediaRowProps {
    title: string;
    items: MediaItem[];
    viewAllLink?: string;
    libraryType?: string;
    /**
     * Optional control rendered at the right of the header, where VIEW ALL sits.
     * Used by the Most Watched row for its Everyone/Me scope toggle. Rows pass
     * either this or viewAllLink — both render, action first, if a row ever needs
     * the two together.
     */
    headerAction?: ReactNode;
}

export default function MediaRow({ title, items, viewAllLink, libraryType, headerAction }: MediaRowProps) {
    const [hoveredId, setHoveredId] = useState<string | null>(null);

    // Sequential left-to-right cascade reveal. The browser parallel-loads images,
    // and the cascade's stuck-timeout handles any out-of-order arrivals.
    const reveal = useSequentialReveal(items?.length ?? 0);

    if (!items || items.length === 0) return null;

    return (
        <div className="mb-8 group/row relative z-10 transition-[z-index] duration-0 hover:z-50">
            {/* Section Header */}
            <div className="flex items-center justify-between mb-16 px-6 relative z-10">
                <h2 className="text-2xl font-bold text-white tracking-tight">
                    {title}
                </h2>
                <div className="flex items-center gap-4">
                {headerAction}
                {viewAllLink && (
                    // Router Link, not a raw <a>: the anchor triggered a full document
                    // reload, throwing away the SPA state and re-fetching the whole
                    // bundle on what should be an in-app navigation.
                    <Link
                        to={viewAllLink}
                        className="flex items-center gap-1 text-sm text-gray-400 hover:text-white transition-colors group focus-visible:text-white focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded px-1"
                    >
                        <span>SEE MORE</span>
                        <ArrowRight size={16} className="group-hover:translate-x-1 transition-transform" />
                    </Link>
                )}
                </div>
            </div>

            <div className="relative overflow-visible">
                <HorizontalScrollList
                    className="-my-24 py-24"
                    gap="gap-8"
                >
                    {/* Scroll Spacer for edge-to-edge alignment */}
                    <div className="flex-shrink-0 w-6 h-1" />

                    {items.map((item, i) => (
                        <div key={item.id} className="flex-shrink-0" style={{ width: '192px' }}>
                            <HoverableMediaCardWrapper
                                item={item}
                                hoveredId={hoveredId}
                                setHoveredId={setHoveredId}
                                libraryType={libraryType}
                                width="100%"
                                groupReady={reveal.isRevealed(i)}
                                onImageLoad={() => reveal.onImageLoad(i)}
                                onImageError={() => reveal.onImageError(i)}
                            />
                        </div>
                    ))}

                    {/* Scroll Spacer for edge-to-edge alignment */}
                    <div className="flex-shrink-0 w-6 h-1" />
                </HorizontalScrollList>
            </div>
        </div>
    );
}

