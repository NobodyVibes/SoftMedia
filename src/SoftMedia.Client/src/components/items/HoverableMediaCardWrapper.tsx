import { motion } from 'framer-motion';
import { type MediaItem } from '../../types';
import MediaCard from './MediaCard';

interface HoverableMediaCardWrapperProps {
    item: MediaItem;
    hoveredId: string | null;
    setHoveredId: (id: string | null) => void;
    baseWidth?: number;
    expandedWidth?: number;
    height?: number;
}

export default function HoverableMediaCardWrapper({
    item,
    hoveredId,
    setHoveredId,
    baseWidth = 180,
    expandedWidth = 260,
    height = 270
}: HoverableMediaCardWrapperProps) {
    return (
        <motion.div
            className={`flex-none relative h-[${height}px] ${hoveredId === item.id ? 'z-50' : 'z-0'}`}
            style={{ height: height }}
            layout
            initial={{ width: baseWidth }}
            animate={{
                width: hoveredId === item.id ? expandedWidth : baseWidth
            }}
            transition={{
                type: "spring",
                stiffness: 300,
                damping: 25
            }}
            onHoverStart={() => setHoveredId(item.id)}
            onHoverEnd={() => setHoveredId(null)}
        >
            <div className="absolute top-1/2 left-1/2 -translate-x-1/2 -translate-y-1/2 w-full">
                <MediaCard item={item} enableHoverScale={false} />
            </div>
        </motion.div>
    );
}
