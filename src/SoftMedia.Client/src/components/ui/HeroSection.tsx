import { Play, Info, Star } from 'lucide-react';
import { motion } from 'framer-motion';

interface HeroSectionProps {
    title: string;
    description: string;
    imageUrl: string;
    year?: number;
    rating?: string;
    duration?: string;
    onPlay?: () => void;
    onMoreInfo?: () => void;
}

export default function HeroSection({
    title,
    description,
    imageUrl,
    year,
    rating,
    duration,
    onPlay,
    onMoreInfo
}: HeroSectionProps) {
    return (
        <div className="relative w-full h-[600px] -mx-6 -mt-6 mb-12 overflow-hidden group">
            {/* Background Image with Parallax Effect */}
            <motion.div
                className="absolute inset-0 bg-cover bg-center scale-105"
                style={{ backgroundImage: `url(${imageUrl})` }}
                initial={{ scale: 1.05 }}
                animate={{ scale: 1.08 }}
                transition={{ duration: 10, repeat: Infinity, repeatType: "reverse" }}
            />

            {/* Multi-layer Gradient Overlays */}
            <div className="absolute inset-0 bg-gradient-to-t from-background via-background/80 to-transparent" />
            <div className="absolute inset-0 bg-gradient-to-r from-background via-background/60 to-transparent" />
            <div className="absolute inset-0 bg-gradient-to-b from-transparent via-transparent to-background" />

            {/* Vignette Effect */}
            <div className="absolute inset-0 shadow-[inset_0_0_200px_rgba(0,0,0,0.8)]" />

            {/* Content Container */}
            <div className="absolute inset-0 flex flex-col justify-end px-12 pb-16 max-w-4xl">
                {/* Metadata Pills */}
                <motion.div
                    className="flex items-center gap-3 mb-4"
                    initial={{ opacity: 0, y: 20 }}
                    animate={{ opacity: 1, y: 0 }}
                    transition={{ delay: 0.2 }}
                >
                    {year && (
                        <span className="text-white/80 text-lg font-semibold tracking-wide">
                            {year}
                        </span>
                    )}
                    {rating && (
                        <span className="px-3 py-1 bg-white/20 backdrop-blur-md border border-white/30 rounded-md text-sm font-bold text-white">
                            {rating}
                        </span>
                    )}
                    {duration && (
                        <span className="text-white/80 text-lg">
                            {duration}
                        </span>
                    )}
                    <div className="flex items-center gap-1 text-yellow-400">
                        <Star size={18} fill="currentColor" />
                        <span className="text-white font-bold">8.2</span>
                    </div>
                </motion.div>

                {/* Title */}
                <motion.h1
                    className="text-7xl font-black text-white mb-6 leading-tight drop-shadow-2xl"
                    style={{
                        textShadow: '0 4px 20px rgba(0,0,0,0.8), 0 0 60px rgba(99,102,241,0.3)'
                    }}
                    initial={{ opacity: 0, y: 30 }}
                    animate={{ opacity: 1, y: 0 }}
                    transition={{ delay: 0.3 }}
                >
                    {title}
                </motion.h1>

                {/* Description */}
                <motion.p
                    className="text-gray-200 text-lg mb-8 line-clamp-3 max-w-2xl leading-relaxed drop-shadow-lg font-medium"
                    initial={{ opacity: 0, y: 20 }}
                    animate={{ opacity: 1, y: 0 }}
                    transition={{ delay: 0.4 }}
                >
                    {description}
                </motion.p>

                {/* Action Buttons */}
                <motion.div
                    className="flex items-center gap-4"
                    initial={{ opacity: 0, y: 20 }}
                    animate={{ opacity: 1, y: 0 }}
                    transition={{ delay: 0.5 }}
                >
                    <motion.button
                        onClick={onPlay}
                        className="group/btn relative px-10 py-4 bg-white rounded-lg font-bold text-lg text-black overflow-hidden shadow-2xl"
                        whileHover={{ scale: 1.05 }}
                        whileTap={{ scale: 0.95 }}
                    >
                        <div className="absolute inset-0 bg-gradient-to-r from-white to-gray-100" />
                        <div className="absolute inset-0 bg-white opacity-0 group-hover/btn:opacity-20 transition-opacity" />
                        <div className="relative flex items-center gap-3">
                            <Play fill="currentColor" size={24} />
                            <span>Play Now</span>
                        </div>
                        {/* Glow Effect */}
                        <div className="absolute inset-0 opacity-0 group-hover/btn:opacity-100 transition-opacity duration-300 blur-xl bg-white/50" />
                    </motion.button>

                    <motion.button
                        onClick={onMoreInfo}
                        className="group/btn relative px-10 py-4 bg-white/10 backdrop-blur-xl border-2 border-white/30 rounded-lg font-bold text-lg text-white overflow-hidden shadow-xl"
                        whileHover={{ scale: 1.05, backgroundColor: 'rgba(255,255,255,0.15)' }}
                        whileTap={{ scale: 0.95 }}
                    >
                        <div className="relative flex items-center gap-3">
                            <Info size={24} />
                            <span>More Info</span>
                        </div>
                    </motion.button>
                </motion.div>
            </div>

            {/* Bottom Fade */}
            <div className="absolute bottom-0 left-0 right-0 h-32 bg-gradient-to-t from-background to-transparent" />
        </div>
    );
}
