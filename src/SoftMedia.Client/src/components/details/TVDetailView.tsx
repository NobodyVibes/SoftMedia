import { useMemo, useState } from 'react';
import { type MediaItem } from '../../types';
import { Tv, Play, ChevronDown, ChevronRight } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import api from '../../services/api';
import { Link } from 'react-router-dom';

interface TVDetailViewProps {
    item: MediaItem;
}

export default function TVDetailView({ item }: TVDetailViewProps) {
    const metadata = item.metadata || {};
    const [expandedSeasons, setExpandedSeasons] = useState<Record<number, boolean>>({});

    const { data: episodes, isLoading } = useQuery({
        queryKey: ['series', item.id, 'episodes'],
        queryFn: async () => {
            const res = await api.get<MediaItem[]>(`/libraries/series/${item.id}/episodes`);
            return res.data;
        }
    });

    const seasons = useMemo(() => {
        if (!episodes) return {};
        const grouped = episodes.reduce((acc, ep) => {
            const season = ep.seasonNumber || 1;
            if (!acc[season]) acc[season] = [];
            acc[season].push(ep);
            return acc;
        }, {} as Record<number, MediaItem[]>);

        // Sort episodes within seasons
        Object.keys(grouped).forEach(key => {
            const k = parseInt(key);
            grouped[k].sort((a, b) => (a.episodeNumber || 0) - (b.episodeNumber || 0));
        });

        return grouped;
    }, [episodes]);

    const toggleSeason = (season: number) => {
        setExpandedSeasons(prev => ({
            ...prev,
            [season]: !prev[season]
        }));
    };

    // Auto-expand first season if loaded
    useMemo(() => {
        if (episodes && Object.keys(seasons).length > 0 && Object.keys(expandedSeasons).length === 0) {
            const firstSeason = Math.min(...Object.keys(seasons).map(k => parseInt(k)));
            setExpandedSeasons({ [firstSeason]: true });
        }
    }, [episodes, seasons]);

    return (
        <div className="grid grid-cols-1 md:grid-cols-3 gap-8">
            <div className="md:col-span-2 space-y-8">
                {/* Show Info */}
                <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                    <h2 className="text-xl font-bold text-white mb-4 flex items-center gap-2">
                        <Tv className="w-5 h-5 text-primary" />
                        Show Info
                    </h2>
                    <div className="grid grid-cols-2 gap-4 text-sm">
                        <div>
                            <span className="block text-gray-400 mb-1">Network</span>
                            <span className="text-white font-medium">{metadata.network || 'Unknown'}</span>
                        </div>
                        <div>
                            <span className="block text-gray-400 mb-1">Status</span>
                            <span className="text-white font-medium">{metadata.status || 'Unknown'}</span>
                        </div>
                        <div>
                            <span className="block text-gray-400 mb-1">First Aired</span>
                            <span className="text-white font-medium">{metadata.premiered || 'Unknown'}</span>
                        </div>
                    </div>
                </div>

                {/* Seasons & Episodes */}
                <div>
                    <h3 className="text-lg font-bold text-white mb-4">Seasons</h3>
                    {isLoading ? (
                        <div className="text-gray-400">Loading episodes...</div>
                    ) : Object.keys(seasons).length === 0 ? (
                        <div className="bg-white/5 rounded-xl p-8 text-center border border-white/10 border-dashed">
                            <p className="text-gray-400">No episodes found.</p>
                        </div>
                    ) : (
                        <div className="space-y-4">
                            {Object.entries(seasons).sort(([a], [b]) => parseInt(a) - parseInt(b)).map(([seasonStr, eps]) => {
                                const season = parseInt(seasonStr);
                                const isExpanded = expandedSeasons[season];

                                return (
                                    <div key={season} className="bg-white/5 rounded-xl border border-white/10 overflow-hidden">
                                        <button
                                            onClick={() => toggleSeason(season)}
                                            className="w-full flex items-center justify-between p-4 hover:bg-white/5 transition-colors"
                                        >
                                            <span className="font-bold text-white">Season {season}</span>
                                            <div className="flex items-center gap-2 text-gray-400">
                                                <span className="text-sm">{eps.length} Episodes</span>
                                                {isExpanded ? <ChevronDown className="w-4 h-4" /> : <ChevronRight className="w-4 h-4" />}
                                            </div>
                                        </button>

                                        {isExpanded && (
                                            <div className="border-t border-white/10 divide-y divide-white/5">
                                                {eps.map(ep => (
                                                    <div key={ep.id} className="p-4 flex items-center gap-4 hover:bg-white/5 transition-colors group">
                                                        <div className="w-8 text-center text-gray-500 font-mono text-sm">
                                                            {ep.episodeNumber}
                                                        </div>
                                                        <div className="flex-grow">
                                                            <h4 className="text-white font-medium group-hover:text-primary transition-colors">
                                                                {ep.title}
                                                            </h4>
                                                            <div className="flex items-center gap-2 text-xs text-gray-400 mt-1">
                                                                {ep.duration && <span>{ep.duration}</span>}
                                                                {ep.watched && <span className="text-green-500">Watched</span>}
                                                            </div>
                                                        </div>
                                                        <Link
                                                            to={`/play/${ep.id}`}
                                                            className="p-2 rounded-full bg-white/10 hover:bg-primary hover:text-white text-gray-300 transition-all opacity-0 group-hover:opacity-100"
                                                        >
                                                            <Play className="w-4 h-4 fill-current" />
                                                        </Link>
                                                    </div>
                                                ))}
                                            </div>
                                        )}
                                    </div>
                                );
                            })}
                        </div>
                    )}
                </div>
            </div>

            {/* Cast */}
            <div>
                <h3 className="text-lg font-bold text-white mb-4">Cast</h3>
                {metadata.cast && Array.isArray(metadata.cast) && metadata.cast.length > 0 ? (
                    <div className="space-y-3">
                        {metadata.cast.slice(0, 10).map((actor: any, i: number) => (
                            <div key={i} className="flex items-center gap-3 bg-white/5 p-2 rounded-lg">
                                <div className="w-10 h-10 rounded-full bg-gray-700 flex items-center justify-center overflow-hidden">
                                    {/* Placeholder for actor image */}
                                    <span className="text-xs text-gray-400">{actor.name?.[0]}</span>
                                </div>
                                <div>
                                    <p className="text-sm font-medium text-white">{actor.name}</p>
                                    <p className="text-xs text-gray-400">{actor.character}</p>
                                </div>
                            </div>
                        ))}
                    </div>
                ) : (
                    <p className="text-gray-500 text-sm">No cast information available.</p>
                )}
            </div>
        </div>
    );
}
