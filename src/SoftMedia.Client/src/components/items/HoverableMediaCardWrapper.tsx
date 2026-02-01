import { motion } from 'framer-motion';
import { type MediaItem, MediaType } from '../../types';
import MediaCard from './MediaCard';

interface HoverableMediaCardWrapperProps {
    item: MediaItem;
    hoveredId: string | null;
    setHoveredId: (id: string | null) => void;
    baseWidth?: number;
    expandedWidth?: number;
    height?: number;
    width?: string | number;
    libraryType?: string;
}

export default function HoverableMediaCardWrapper({
    item,
    hoveredId,
    setHoveredId,
    // baseWidth and expandedWidth are available but not currently used
    // They are part of the prop interface for future flexibility
    baseWidth: _baseWidth = 180,
    expandedWidth: _expandedWidth,
    height,
    width = '100%',
    libraryType
}: HoverableMediaCardWrapperProps) {
    const isHovered = hoveredId === item.id;
    const isAudio = libraryType === 'Music' ||
        item.type === MediaType.Audio ||
        item.type === MediaType.Artist ||
        item.type === MediaType.Album;

    const finalHeight = height ?? (isAudio ? 290 : 380);

    return (
        <motion.div
            className="relative"
            style={{
                width: width,
                height: finalHeight,
                // Maintain original position in grid
                position: 'relative',
            }}
            onHoverStart={() => setHoveredId(item.id)}
            onHoverEnd={() => setHoveredId(null)}
        >
            {/* Card container with transform-based scaling */}
            <motion.div
                className="absolute inset-0 origin-center"
                style={{
                    zIndex: isHovered ? 50 : 0,
                }}
                animate={{
                    scale: isHovered ? 1.15 : 1,
                }}
                transition={{
                    type: "spring",
                    stiffness: 400,
                    damping: 30
                }}
            >
                <MediaCard item={item} enableHoverScale={false} libraryType={libraryType} />
            </motion.div>
        </motion.div>
    );
}

