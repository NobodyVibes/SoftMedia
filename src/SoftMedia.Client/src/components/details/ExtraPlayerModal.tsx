import { useEffect } from 'react';
import { X } from 'lucide-react';
import { attachAuthToApiUrl } from '../../lib/mediaImageUrl';
import type { MediaExtra } from '../../hooks/useExtras';

/**
 * Lightweight direct-play modal for companion clips (NR-WI-014). House modal
 * style: explicit close button + Escape, no click-away divs (a11y guard).
 */
export function ExtraPlayerModal({ mediaId, extra, onClose }: {
    mediaId: string;
    extra: MediaExtra;
    onClose: () => void;
}) {
    useEffect(() => {
        const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
        window.addEventListener('keydown', onKey);
        return () => window.removeEventListener('keydown', onKey);
    }, [onClose]);

    return (
        <div className="fixed inset-0 z-50 bg-black/90 flex items-center justify-center p-4">
            <div className="relative w-full max-w-5xl">
                <div className="flex items-center justify-between mb-2">
                    <span className="text-white font-medium">{extra.title}</span>
                    <button
                        onClick={onClose}
                        aria-label="Close"
                        className="p-3 min-w-[44px] min-h-[44px] flex items-center justify-center rounded-full bg-white/10 hover:bg-white/20 text-white transition-colors"
                    >
                        <X className="w-5 h-5" />
                    </button>
                </div>
                {/* Direct play; the media token rides in the query (media route). */}
                <video
                    src={attachAuthToApiUrl(`/api/v1/stream/${mediaId}/extras/${extra.index}`)}
                    controls
                    autoPlay
                    className="w-full max-h-[80vh] rounded-xl bg-black"
                />
            </div>
        </div>
    );
}
