import { useState } from 'react';
import { AnimatePresence } from 'framer-motion';
import { Clapperboard, Play } from 'lucide-react';
import { useExtras, findTrailer, type MediaExtra } from '../../hooks/useExtras';
import { ExtraPlayerModal } from './ExtraPlayerModal';

/**
 * NR-WI-014 — bonus content (samples, featurettes, behind-the-scenes) for a movie
 * or series. The trailer itself is NOT listed here: it's promoted to a "Trailer"
 * button beside Play in MediaDetailLayout — this row is everything else. Renders
 * nothing when the title has no non-trailer extras.
 */
export function ExtrasSection({ mediaId, itemType }: { mediaId: string; itemType: string | undefined }) {
    const [playing, setPlaying] = useState<MediaExtra | null>(null);
    const { data: extras = [] } = useExtras(mediaId, itemType);

    const trailer = findTrailer(extras);
    const bonus = extras.filter((e) => e.index !== trailer?.index);

    if (bonus.length === 0) return null;

    return (
        <div className="mt-10">
            <h3 className="text-lg font-semibold text-white mb-4 flex items-center gap-2">
                <Clapperboard className="w-5 h-5 text-orange-400" />
                Extras
            </h3>
            <div className="flex flex-wrap gap-3">
                {bonus.map((extra) => (
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

            <AnimatePresence>
                {playing && (
                    <ExtraPlayerModal mediaId={mediaId} extra={playing} onClose={() => setPlaying(null)} />
                )}
            </AnimatePresence>
        </div>
    );
}
