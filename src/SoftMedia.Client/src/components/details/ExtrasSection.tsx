import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Clapperboard, Play, X } from 'lucide-react';
import api from '../../services/api';
import { attachAuthToApiUrl } from '../../lib/mediaImageUrl';

interface MediaExtra {
    index: number;
    title: string;
    kind: string;
    fileName: string;
    sizeBytes: number;
}

/**
 * NR-WI-014 — companion clips (trailers, samples, featurettes) for a movie or series.
 * Server-probed from the filesystem; renders nothing when the title has none.
 * Playback is direct-play in a lightweight modal — extras are small clips and don't
 * go through the transcode pipeline (v1 limitation, documented).
 */
export function ExtrasSection({ mediaId }: { mediaId: string }) {
    const [playing, setPlaying] = useState<MediaExtra | null>(null);

    // Escape closes the player (house modal style: explicit close affordances, no
    // click-away divs — those fail the Universal Client a11y guard).
    useEffect(() => {
        if (!playing) return;
        const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setPlaying(null); };
        window.addEventListener('keydown', onKey);
        return () => window.removeEventListener('keydown', onKey);
    }, [playing]);

    const { data: extras = [] } = useQuery<MediaExtra[]>({
        queryKey: ['extras', mediaId],
        queryFn: async () => (await api.get<MediaExtra[]>(`/stream/${mediaId}/extras`)).data,
        staleTime: 60_000,
    });

    if (extras.length === 0) return null;

    return (
        <div className="mt-10">
            <h3 className="text-lg font-semibold text-white mb-4 flex items-center gap-2">
                <Clapperboard className="w-5 h-5 text-orange-400" />
                Extras
            </h3>
            <div className="flex flex-wrap gap-3">
                {extras.map((extra) => (
                    <button
                        key={extra.index}
                        onClick={() => setPlaying(extra)}
                        className="group flex items-center gap-3 bg-white/5 hover:bg-white/10 border border-white/10 rounded-xl px-4 py-3 min-h-[44px] transition-colors text-left"
                    >
                        <div className="p-2 rounded-full bg-orange-500/15 text-orange-400 group-hover:scale-110 transition-transform">
                            <Play className="w-4 h-4 fill-current" />
                        </div>
                        <div>
                            <div className="text-white text-sm font-medium">{extra.title}</div>
                            <div className="text-gray-500 text-xs capitalize">{extra.kind}</div>
                        </div>
                    </button>
                ))}
            </div>

            {playing && (
                <div className="fixed inset-0 z-50 bg-black/90 flex items-center justify-center p-4">
                    <div className="relative w-full max-w-5xl">
                        <div className="flex items-center justify-between mb-2">
                            <span className="text-white font-medium">{playing.title}</span>
                            <button
                                onClick={() => setPlaying(null)}
                                aria-label="Close"
                                className="p-3 min-w-[44px] min-h-[44px] flex items-center justify-center rounded-full bg-white/10 hover:bg-white/20 text-white transition-colors"
                            >
                                <X className="w-5 h-5" />
                            </button>
                        </div>
                        {/* Direct play; the media token rides in the query (media route). */}
                        <video
                            src={attachAuthToApiUrl(`/api/v1/stream/${mediaId}/extras/${playing.index}`)}
                            controls
                            autoPlay
                            className="w-full max-h-[80vh] rounded-xl bg-black"
                        />
                    </div>
                </div>
            )}
        </div>
    );
}
