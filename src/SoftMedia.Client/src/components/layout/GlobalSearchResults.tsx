import { useNavigate } from 'react-router-dom';
import { motion } from 'framer-motion';
import { Play, Film, Tv, Music, BookOpen, Gamepad2, Image } from 'lucide-react';
import type { GlobalSearchResult } from '../../services/searchService';

interface GlobalSearchResultsProps {
    results: GlobalSearchResult[];
    isLoading: boolean;
    onClose: () => void;
}

const libraryIcons: Record<string, React.ReactNode> = {
    Movie: <Film size={14} />,
    TV: <Tv size={14} />,
    Music: <Music size={14} />,
    Book: <BookOpen size={14} />,
    Game: <Gamepad2 size={14} />,
    Photo: <Image size={14} />,
};

export default function GlobalSearchResults({ results, isLoading, onClose }: GlobalSearchResultsProps) {
    const navigate = useNavigate();

    const handlePlay = (e: React.MouseEvent, itemId: string) => {
        e.preventDefault();
        e.stopPropagation();
        onClose();
        navigate(`/player/${itemId}`);
    };

    const handleItemClick = (itemId: string) => {
        onClose();
        navigate(`/media/${itemId}`);
    };

    if (isLoading) {
        return (
            <motion.div
                initial={{ opacity: 0, y: -10 }}
                animate={{ opacity: 1, y: 0 }}
                exit={{ opacity: 0, y: -10 }}
                className="absolute top-full left-0 right-0 mt-2 bg-[#1a1a1a] border border-white/10 rounded-xl shadow-2xl overflow-hidden z-50"
            >
                <div className="p-4 text-center text-gray-400">
                    <div className="animate-pulse">Searching...</div>
                </div>
            </motion.div>
        );
    }

    if (results.length === 0) {
        return null;
    }

    return (
        <motion.div
            initial={{ opacity: 0, y: -10 }}
            animate={{ opacity: 1, y: 0 }}
            exit={{ opacity: 0, y: -10 }}
            transition={{ duration: 0.2 }}
            className="absolute top-full left-0 right-0 mt-2 bg-[#1a1a1a] border border-white/10 rounded-xl shadow-2xl overflow-hidden z-50 max-h-[70vh] overflow-y-auto"
        >
            {results.map((group) => (
                <div key={group.libraryId}>
                    {/* Library Header */}
                    <div className="px-4 py-2 bg-gradient-to-r from-primary/10 to-secondary/10 border-b border-white/5 flex items-center gap-2">
                        <span className="text-primary">
                            {libraryIcons[group.libraryType] || <Film size={14} />}
                        </span>
                        <span className="text-xs font-semibold text-gray-300 uppercase tracking-wider">
                            {group.libraryName}
                        </span>
                    </div>

                    {/* Library Items */}
                    <div className="divide-y divide-white/5">
                        {group.items.map((item) => (
                            <button
                                key={item.id}
                                onClick={() => handleItemClick(item.id)}
                                className="w-full px-4 py-3 flex items-center gap-3 hover:bg-white/5 transition-colors group text-left"
                            >
                                {/* Thumbnail */}
                                <div className="w-10 h-14 bg-gradient-to-br from-primary/20 to-secondary/20 rounded overflow-hidden flex-shrink-0">
                                    {item.posterPath ? (
                                        <img
                                            src={item.posterPath}
                                            alt={item.title}
                                            className="w-full h-full object-cover"
                                        />
                                    ) : (
                                        <div className="w-full h-full flex items-center justify-center text-gray-500">
                                            {libraryIcons[group.libraryType] || <Film size={16} />}
                                        </div>
                                    )}
                                </div>

                                {/* Title & Year */}
                                <div className="flex-1 min-w-0">
                                    <p className="text-sm font-medium text-white truncate group-hover:text-primary transition-colors">
                                        {item.title}
                                    </p>
                                    {item.year && (
                                        <p className="text-xs text-gray-400">{item.year}</p>
                                    )}
                                </div>

                                {/* Play Button */}
                                <motion.button
                                    whileHover={{ scale: 1.1 }}
                                    whileTap={{ scale: 0.9 }}
                                    onClick={(e) => handlePlay(e, item.id)}
                                    className="p-2 bg-primary/20 hover:bg-primary text-primary hover:text-white rounded-full transition-colors opacity-0 group-hover:opacity-100"
                                >
                                    <Play size={14} fill="currentColor" />
                                </motion.button>
                            </button>
                        ))}
                    </div>
                </div>
            ))}
        </motion.div>
    );
}
