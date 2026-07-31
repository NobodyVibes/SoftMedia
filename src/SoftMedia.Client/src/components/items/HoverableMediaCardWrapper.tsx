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
    /** Optional batch/cascade coordination — forwarded to MediaCard → LoadingImage. */
    groupReady?: boolean;
    onImageLoad?: () => void;
    onImageError?: () => void;
}

export default function HoverableMediaCardWrapper({
    item,
    hoveredId,
    setHoveredId,
    // baseWidth/expandedWidth stay on the prop interface for callers, but this
    // wrapper doesn't consume them — so they are deliberately NOT destructured.
    height,
    width = 192,
    libraryType,
    groupReady,
    onImageLoad,
    onImageError,
}: HoverableMediaCardWrapperProps) {
    const isHovered = hoveredId === item.id;
    const isAudio = libraryType === 'Music' ||
        item.type === MediaType.Audio ||
        item.type === MediaType.Artist ||
        item.type === MediaType.Album;

    // Fixed heights to prevent info-area squashing
    const finalHeight = height ?? (isAudio ? 300 : 400);

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
                <MediaCard
                    item={item}
                    enableHoverScale={false}
                    libraryType={libraryType}
                    groupReady={groupReady}
                    onImageLoad={onImageLoad}
                    onImageError={onImageError}
                />
            </motion.div>
        </motion.div>
    );
}

