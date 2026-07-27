import { useState } from 'react';
import { ListMusic } from 'lucide-react';
import { cn } from '../../lib/utils';
import { resolveCardPosterUrl, resolveHeroPosterUrl } from '../../lib/mediaImageUrl';
import { useMediaTokenRefresh } from '../../hooks/useMediaTokenRefresh';

interface PlaylistCoverProps {
    /**
     * Cover-art paths in play order. Server-supplied on the index
     * (PlaylistSummary.coverImagePaths); derived from the loaded items on the
     * detail page. Anything past the fourth entry is ignored.
     */
    coverPaths?: string[] | null;
    /** Requested thumbnail size. Cards need ~300px, the detail header ~500px. */
    size?: 'card' | 'hero';
    /** Sizing/rounding for the outer square — the caller owns the footprint. */
    className?: string;
    /** Icon size for the artless fallback tile. */
    iconClassName?: string;
}

/**
 * Artwork for a playlist, which owns no image of its own.
 *
 * Playlists were the last surface in the app still represented by a flat icon
 * tile while every card, hero and detail page led with real artwork. This
 * borrows the album covers already in the playlist:
 *
 *   - four or more distinct covers → 2×2 mosaic,
 *   - one to three → the first cover, full bleed (a mosaic with a hole in it
 *     reads as a loading failure, not as a design),
 *   - none → the original brand-gradient tile.
 *
 * Covers that fail to load are dropped from the layout rather than left as
 * broken tiles, so a playlist whose single cover 404s degrades to the gradient
 * instead of a grey box.
 */
export function PlaylistCover({ coverPaths, size = 'card', className, iconClassName }: PlaylistCoverProps) {
    // Media URLs below embed the media token; re-render when it rotates so a
    // stale token can't leave the artwork permanently broken.
    useMediaTokenRefresh();
    const [failed, setFailed] = useState<string[]>([]);

    const resolve = size === 'hero' ? resolveHeroPosterUrl : resolveCardPosterUrl;
    const usable = (coverPaths ?? []).filter(p => !!p && !failed.includes(p)).slice(0, 4);

    // Cards appear by the dozen in a scrolling grid, each with up to four tiles, so
    // they defer. The hero is the page's lead image and above the fold — deferring
    // it would only delay the largest paint.
    const loading = size === 'hero' ? 'eager' : 'lazy';

    if (usable.length === 0) {
        return (
            <div className={cn('bg-brand-gradient flex items-center justify-center', className)}>
                <ListMusic className={cn('text-white/90', iconClassName ?? 'w-1/2 h-1/2')} />
            </div>
        );
    }

    const markFailed = (path: string) => setFailed(prev => (prev.includes(path) ? prev : [...prev, path]));

    if (usable.length < 4) {
        return (
            <div className={cn('overflow-hidden bg-white/5', className)}>
                <img
                    src={resolve(usable[0]) ?? undefined}
                    onError={() => markFailed(usable[0])}
                    referrerPolicy="no-referrer"
                    loading={loading}
                    decoding="async"
                    alt=""
                    className="w-full h-full object-cover"
                />
            </div>
        );
    }

    return (
        <div className={cn('overflow-hidden bg-white/5 grid grid-cols-2 grid-rows-2', className)}>
            {usable.map(path => (
                <img
                    key={path}
                    src={resolve(path) ?? undefined}
                    onError={() => markFailed(path)}
                    referrerPolicy="no-referrer"
                    loading={loading}
                    decoding="async"
                    alt=""
                    className="w-full h-full object-cover"
                />
            ))}
        </div>
    );
}

export default PlaylistCover;
