import { Link } from 'react-router-dom';
import { motion } from 'framer-motion';
import { Play, ListMusic } from 'lucide-react';
import { type MediaItem } from '../../types';
import QualityBadge from '../ui/QualityBadge';
import ProgressBar from '../ui/ProgressBar';
import { useAudioStore } from '../../store/audioStore';

interface MediaCardProps {
    item: MediaItem;
    libraryType?: string;
}

const genreColors: Record<string, string> = {
    'Fantasy': 'from-purple-500 to-pink-500',
    'Action': 'from-red-500 to-orange-500',
    'Horror': 'from-gray-700 to-red-700',
    'Comedy': 'from-yellow-400 to-orange-400',
    'Drama': 'from-blue-500 to-cyan-500',
    'Sci-Fi': 'from-cyan-500 to-blue-600',
    'Thriller': 'from-gray-600 to-red-600',
    'Animation': 'from-pink-400 to-purple-500',
    'Mystery': 'from-indigo-600 to-purple-600',
};

export default function MediaCard({ item, libraryType }: MediaCardProps) {
    const primaryGenre = item.genres?.[0] || 'Drama';
    const glowColor = genreColors[primaryGenre] || 'from-primary to-secondary';
    const { playTrack, addToQueue } = useAudioStore();

    const isAudio = libraryType === 'Music';

    const handlePlay = (e: React.MouseEvent) => {
        if (isAudio) {
            e.preventDefault();
            playTrack(item);
        }
    };

    const handleAddToQueue = (e: React.MouseEvent) => {
        e.preventDefault();
        e.stopPropagation();
        addToQueue(item);
    };

    const CardContent = (
        <motion.div
            className="relative aspect-[2/3] overflow-hidden rounded-xl shadow-xl"
            initial={{ opacity: 0, y: 20 }}
            animate={{ opacity: 1, y: 0 }}
            whileHover={{
                scale: 1.08,
                y: -12,
                rotateY: 2,
                transition: {
                    type: "spring",
                    stiffness: 300,
                    damping: 20
                }
            }}
        >
            {/* Poster Image */}
            {item.posterPath ? (
                <motion.img
                    src={item.posterPath}
                    alt={item.title}
                    className="h-full w-full object-cover"
                    loading="lazy"
                    initial={{ opacity: 0 }}
                    animate={{ opacity: 1 }}
                    transition={{ duration: 0.3 }}
                />
            ) : (
                <div className="flex h-full w-full items-center justify-center bg-slate-800 text-slate-500">
                    <span className="text-4xl">?</span>
                </div>
            )}

            {/* Quality Badge */}
            <div className="absolute top-3 right-3 opacity-0 group-hover/card:opacity-100 transition-opacity duration-200">
                <QualityBadge quality={item.quality} />
            </div>

            {/* Play Button Overlay with Ripple */}
            <div className="absolute inset-0 bg-black/0 group-hover/card:bg-black/50 transition-all duration-300 flex items-center justify-center">
                <motion.div
                    initial={{ scale: 0, opacity: 0 }}
                    className="opacity-0 group-hover/card:opacity-100"
                    whileHover={{ scale: 1 }}
                    transition={{ type: "spring", stiffness: 400, damping: 17 }}
                >
                    <div className="relative">
                        {/* Pulsing Ring */}
                        <motion.div
                            className={`absolute inset-0 rounded-full bg-gradient-to-br ${glowColor} opacity-50 blur-md`}
                            animate={{
                                scale: [1, 1.2, 1],
                                opacity: [0.5, 0.8, 0.5]
                            }}
                            transition={{
                                duration: 2,
                                repeat: Infinity,
                                ease: "easeInOut"
                            }}
                        />
                        {/* Play Button */}
                        <div
                            className="relative bg-white rounded-full p-5 shadow-2xl cursor-pointer"
                            onClick={handlePlay}
                        >
                            <Play className="w-10 h-10 text-black fill-black" />
                        </div>

                        {/* Add to Queue Button (Audio Only) */}
                        {isAudio && (
                            <div
                                className="absolute -right-12 top-2 bg-gray-800 rounded-full p-2 shadow-xl cursor-pointer hover:bg-gray-700 transition"
                                onClick={handleAddToQueue}
                                title="Add to Queue"
                            >
                                <ListMusic className="w-5 h-5 text-white" />
                            </div>
                        )}
                    </div>
                </motion.div>
            </div>

            {/* Info Overlay - Bottom with Spring Animation */}
            <motion.div
                className="absolute inset-x-0 bottom-0 bg-gradient-to-t from-black via-black/95 to-transparent p-4"
                initial={{ y: 60, opacity: 0 }}
                animate={{ y: 60, opacity: 0 }}
                whileHover={{ y: 0, opacity: 1 }}
                transition={{ type: "spring", stiffness: 300, damping: 25 }}
            >
                <h3 className="text-white font-bold text-base line-clamp-2 mb-2 drop-shadow-lg">
                    {item.title}
                </h3>
                <div className="flex items-center gap-2 text-xs text-gray-300 flex-wrap mb-2">
                    {item.year && <span className="font-semibold">{item.year}</span>}
                    {item.duration && (
                        <>
                            {item.year && <span>•</span>}
                            <span>{item.duration}</span>
                        </>
                    )}
                    {item.rating && (
                        <>
                            <span>•</span>
                            <span className="px-2 py-0.5 bg-gray-700/90 rounded text-xs font-bold border border-gray-600">
                                {item.rating}
                            </span>
                        </>
                    )}
                </div>
                {/* Genre Pills */}
                {item.genres && item.genres.length > 0 && (
                    <div className="flex gap-1.5 flex-wrap">
                        {item.genres.slice(0, 2).map((genre) => (
                            <span
                                key={genre}
                                className={`px-2 py-1 text-xs font-bold text-white rounded-full bg-gradient-to-r ${genreColors[genre] || 'from-gray-600 to-gray-700'} shadow-lg`}
                            >
                                {genre}
                            </span>
                        ))}
                    </div>
                )}
            </motion.div>

            {/* Progress Bar */}
            {item.progress !== undefined && item.progress > 0 && (
                <ProgressBar progress={item.progress} />
            )}

            {/* Enhanced Border */}
            <div className="absolute inset-0 ring-2 ring-transparent group-hover/card:ring-white/20 rounded-xl pointer-events-none transition-all duration-300" />
        </motion.div>
    );

    if (isAudio) {
        return (
            <div className="block group/card cursor-pointer">
                {CardContent}
            </div>
        );
    }

    const isBook = libraryType === 'Book';
    const linkTarget = isBook ? `/read/${item.id}` : `/media/${item.id}`;

    return (
        <Link to={linkTarget} className="block group/card">
            {CardContent}
        </Link>
    );
}
