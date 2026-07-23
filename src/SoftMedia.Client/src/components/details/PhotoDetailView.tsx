import { useEffect, useRef } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import api from '../../services/api';
import { type MediaItem, type PagedResult } from '../../types';
import { motion } from 'framer-motion';
import { attachAuthToApiUrl } from '../../lib/mediaImageUrl';
import { useLocalPreferences } from '../../hooks/useLocalPreferences';
import { buildPhotoNavSuffix } from '../../lib/photoNav';
import { Camera, MapPin, Aperture, Clock, ChevronLeft, ChevronRight, Expand, Maximize2, Pause, Play, X } from 'lucide-react';

interface PhotoDetailViewProps {
    item: MediaItem;
}

export default function PhotoDetailView({ item }: PhotoDetailViewProps) {
    const navigate = useNavigate();

    // Opened from an album grid? Then ← / → page within THAT album, not the whole
    // library — and navigation preserves the album context. `album` may be "" (the
    // root album), so presence is the signal, not truthiness.
    const [searchParams, setSearchParams] = useSearchParams();
    const albumKey = searchParams.has('album') ? searchParams.get('album')! : null;
    const slideshow = searchParams.get('slideshow') === '1';
    // Fullscreen rides in the URL like the slideshow — component state wouldn't
    // survive the per-photo route change.
    const fullscreen = searchParams.get('fs') === '1';
    const lightboxRef = useRef<HTMLDivElement>(null);

    // Navigation suffix carries the album scope, the slideshow flag, and the
    // fullscreen flag across per-photo navigation.
    const albumSuffix = buildPhotoNavSuffix(albumKey, slideshow, fullscreen);

    // Updater form: listeners created on one photo can fire after navigating to
    // another — a captured searchParams snapshot would resurrect stale params.
    const setFlag = (name: string, on: boolean) => {
        setSearchParams(prev => {
            const next = new URLSearchParams(prev);
            if (on) next.set(name, '1');
            else next.delete(name);
            return next;
        }, { replace: true });
    };
    const toggleSlideshow = () => setFlag('slideshow', !slideshow);
    const exitFullscreen = () => setFlag('fs', false);

    // Best-effort REAL browser fullscreen on top of the overlay: requested when the
    // lightbox mounts (still within the click's transient activation), released on
    // unmount. If the browser refuses, the fixed inset-0 overlay still covers the
    // viewport — the feature degrades to a pseudo-fullscreen, not a failure.
    useEffect(() => {
        if (!fullscreen) return;
        lightboxRef.current?.requestFullscreen?.().catch(() => { /* pseudo-fullscreen */ });

        // The browser's own exit (Esc under real fullscreen fires no keydown here)
        // must also clear the flag, or the overlay would linger.
        const onFsChange = () => {
            if (!document.fullscreenElement) exitFullscreen();
        };
        document.addEventListener('fullscreenchange', onFsChange);
        return () => {
            document.removeEventListener('fullscreenchange', onFsChange);
            if (document.fullscreenElement) document.exitFullscreen().catch(() => { });
        };
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [fullscreen]);

    const { data: libraryItems } = useQuery({
        queryKey: albumKey !== null
            ? ['photoAlbum', item.libraryId, albumKey]
            : ['library', item.libraryId, 'items', 'navigation'],
        queryFn: async () => {
            if (albumKey !== null) {
                const response = await api.get<MediaItem[]>(
                    `/photos/albums/photos?libraryId=${item.libraryId}&key=${encodeURIComponent(albumKey)}`);
                return response.data;
            }
            const response = await api.get<PagedResult<MediaItem>>(`/libraries/${item.libraryId}/items`, {
                params: { page: 1, pageSize: 1000 }
            });
            return response.data.items;
        },
        enabled: !!item.libraryId
    });

    const currentIndex = libraryItems?.findIndex(i => i.id === item.id) ?? -1;
    const prevItem = currentIndex > 0 ? libraryItems?.[currentIndex - 1] : null;
    const nextItem = currentIndex !== -1 && currentIndex < (libraryItems?.length ?? 0) - 1 ? libraryItems?.[currentIndex + 1] : null;

    // Arrow keys page through the library like a slideshow. Skipped while an
    // input has focus so the global search box keeps its cursor keys.
    useEffect(() => {
        const onKeyDown = (e: KeyboardEvent) => {
            const target = e.target as HTMLElement | null;
            if (target && ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName)) return;
            if (e.key === 'ArrowLeft' && prevItem) navigate(`/media/${prevItem.id}${albumSuffix}`);
            if (e.key === 'ArrowRight' && nextItem) navigate(`/media/${nextItem.id}${albumSuffix}`);
            // Escape leaves fullscreen (under REAL browser fullscreen the browser eats
            // Esc itself; the fullscreenchange listener handles that path instead).
            if (e.key === 'Escape' && fullscreen) exitFullscreen();
        };
        window.addEventListener('keydown', onKeyDown);
        return () => window.removeEventListener('keydown', onKeyDown);
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [prevItem, nextItem, navigate, albumSuffix, fullscreen]);

    // Slideshow: advance every 5s while the ?slideshow=1 flag rides along the
    // navigation (component state wouldn't survive the route change). LOOPS back to
    // the first photo at the end — the old stop-at-end rule meant pressing Play on
    // the LAST photo (where date-less GIFs usually sort) cancelled itself instantly.
    // It only turns itself off when there's genuinely nothing to advance to: a
    // one-photo scope, or a photo missing from the nav list entirely.
    const slideshowTarget = nextItem
        ?? (currentIndex !== -1 && (libraryItems?.length ?? 0) > 1 ? libraryItems![0] : null);
    useEffect(() => {
        if (!slideshow) return;
        if (!slideshowTarget) {
            setFlag('slideshow', false);
            return;
        }
        const timer = window.setTimeout(() => navigate(`/media/${slideshowTarget.id}${albumSuffix}`), 5000);
        return () => window.clearTimeout(timer);
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [slideshow, slideshowTarget?.id, albumSuffix]);

    const metadata = item.metadata || {};
    const camera = metadata.camera as string;
    const iso = metadata.iso as string;
    const fstop = metadata.fstop as string;
    const exposure = metadata.exposure as string;
    const dateTaken = metadata.dateTaken as string;
    const gps = metadata.gps as string;

    // The photo itself, at original resolution. The <img> can't send an
    // Authorization header, so the media token travels in the query string
    // (the /api/v1/photos route is in the server's media-route allowlist).
    const fullImageUrl = attachAuthToApiUrl(`/api/v1/photos/${item.id}/image`);

    // Entrance transition per the device preference. Keyed on item.id so paging
    // (arrows, keys, slideshow) replays it; entrance-only by design — the previous
    // photo is gone by the time the route changes, so a true crossfade isn't
    // possible without holding both images, which "simple" doesn't warrant.
    const { preferences } = useLocalPreferences();
    const entranceProps = (() => {
        switch (preferences.slideshowTransition) {
            case 'fade':
                return { initial: { opacity: 0 }, animate: { opacity: 1 }, transition: { duration: 0.5, ease: 'easeOut' as const } };
            case 'slide':
                return { initial: { opacity: 0, x: 48 }, animate: { opacity: 1, x: 0 }, transition: { duration: 0.45, ease: 'easeOut' as const } };
            case 'zoom':
                // Fade in quickly, then drift the scale for the whole 5s dwell —
                // a restrained Ken Burns.
                return {
                    initial: { opacity: 0, scale: 1 },
                    animate: { opacity: 1, scale: 1.05 },
                    transition: { opacity: { duration: 0.5 }, scale: { duration: 6, ease: 'linear' as const } },
                };
            default:
                return {}; // 'none' — instant
        }
    })();

    return (
        <div className="space-y-8">
            {/* The photo IS the content — show it full-width, letterboxed. */}
            <div className="relative group rounded-xl overflow-hidden bg-black/60 border border-white/10">
                <motion.img
                    key={item.id}
                    {...entranceProps}
                    src={fullImageUrl}
                    alt={item.title}
                    className="w-full max-h-[75vh] object-contain"
                />

                {/* Hover chevrons — same targets as the ← / → keys */}
                {prevItem && (
                    <button
                        onClick={() => navigate(`/media/${prevItem.id}${albumSuffix}`)}
                        aria-label="Previous photo"
                        className="absolute left-3 top-1/2 -translate-y-1/2 p-3 min-w-[44px] min-h-[44px] flex items-center justify-center rounded-full bg-black/60 text-white opacity-0 group-hover:opacity-100 focus-visible:opacity-100 transition-opacity hover:bg-black/80"
                    >
                        <ChevronLeft className="w-6 h-6" />
                    </button>
                )}
                {nextItem && (
                    <button
                        onClick={() => navigate(`/media/${nextItem.id}${albumSuffix}`)}
                        aria-label="Next photo"
                        className="absolute right-3 top-1/2 -translate-y-1/2 p-3 min-w-[44px] min-h-[44px] flex items-center justify-center rounded-full bg-black/60 text-white opacity-0 group-hover:opacity-100 focus-visible:opacity-100 transition-opacity hover:bg-black/80"
                    >
                        <ChevronRight className="w-6 h-6" />
                    </button>
                )}

                <div className="absolute top-3 right-3 flex gap-2">
                    <button
                        onClick={() => setFlag('fs', true)}
                        title="Fullscreen"
                        aria-label="View fullscreen"
                        className="p-3 min-w-[44px] min-h-[44px] flex items-center justify-center rounded-full bg-black/60 text-white opacity-0 group-hover:opacity-100 focus-visible:opacity-100 transition-opacity hover:bg-black/80"
                    >
                        <Expand className="w-5 h-5" />
                    </button>
                    <button
                        onClick={toggleSlideshow}
                        title={slideshow ? 'Pause slideshow' : 'Start slideshow'}
                        aria-label={slideshow ? 'Pause slideshow' : 'Start slideshow'}
                        className={`p-3 min-w-[44px] min-h-[44px] flex items-center justify-center rounded-full bg-black/60 text-white transition-opacity hover:bg-black/80 ${slideshow ? 'opacity-100' : 'opacity-0 group-hover:opacity-100 focus-visible:opacity-100'}`}
                    >
                        {slideshow ? <Pause className="w-5 h-5" /> : <Play className="w-5 h-5 fill-current" />}
                    </button>
                    <a
                        href={fullImageUrl}
                        target="_blank"
                        rel="noreferrer"
                        title="Open original in new tab"
                        aria-label="Open original in new tab"
                        className="p-3 min-w-[44px] min-h-[44px] flex items-center justify-center rounded-full bg-black/60 text-white opacity-0 group-hover:opacity-100 focus-visible:opacity-100 transition-opacity hover:bg-black/80"
                    >
                        <Maximize2 className="w-5 h-5" />
                    </a>
                </div>
            </div>

            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
                {camera && (
                    <div className="bg-white/5 p-4 rounded-xl border border-white/10 flex items-center gap-3">
                        <Camera className="w-5 h-5 text-blue-400" />
                        <div>
                            <div className="text-xs text-gray-400 uppercase">Camera</div>
                            <div className="text-white font-medium">{camera}</div>
                        </div>
                    </div>
                )}
                {(iso || fstop || exposure) && (
                    <div className="bg-white/5 p-4 rounded-xl border border-white/10 flex items-center gap-3">
                        <Aperture className="w-5 h-5 text-yellow-400" />
                        <div>
                            <div className="text-xs text-gray-400 uppercase">Settings</div>
                            <div className="text-white font-medium">
                                {[fstop && `f/${fstop}`, exposure && `${exposure}s`, iso && `ISO ${iso}`].filter(Boolean).join(' • ')}
                            </div>
                        </div>
                    </div>
                )}
                {dateTaken && (
                    <div className="bg-white/5 p-4 rounded-xl border border-white/10 flex items-center gap-3">
                        <Clock className="w-5 h-5 text-green-400" />
                        <div>
                            <div className="text-xs text-gray-400 uppercase">Date Taken</div>
                            <div className="text-white font-medium">{dateTaken}</div>
                        </div>
                    </div>
                )}
                {gps && (
                    <div className="bg-white/5 p-4 rounded-xl border border-white/10 flex items-center gap-3">
                        <MapPin className="w-5 h-5 text-red-400" />
                        <div>
                            <div className="text-xs text-gray-400 uppercase">Location</div>
                            <div className="text-white font-medium truncate" title={gps}>{gps}</div>
                        </div>
                    </div>
                )}
                {item.width && item.height && (
                    <div className="bg-white/5 p-4 rounded-xl border border-white/10 flex items-center gap-3">
                        <Maximize2 className="w-5 h-5 text-purple-400" />
                        <div>
                            <div className="text-xs text-gray-400 uppercase">Resolution</div>
                            <div className="text-white font-medium">{item.width} × {item.height}</div>
                        </div>
                    </div>
                )}
            </div>

            {/* Navigation Buttons */}
            <div className="flex justify-between items-center pt-8 border-t border-white/10">
                {prevItem ? (
                    <button
                        onClick={() => navigate(`/media/${prevItem!.id}${albumSuffix}`)}
                        className="flex items-center gap-2 text-gray-400 hover:text-white transition-colors group min-h-[44px]"
                    >
                        <ChevronLeft className="w-5 h-5 group-hover:-translate-x-1 transition-transform" />
                        <div className="text-left">
                            <div className="text-xs text-gray-500">Previous</div>
                            <div className="font-medium max-w-[150px] truncate">{prevItem.title}</div>
                        </div>
                    </button>
                ) : <div />}

                {nextItem && (
                    <button
                        onClick={() => navigate(`/media/${nextItem!.id}${albumSuffix}`)}
                        className="flex items-center gap-2 text-gray-400 hover:text-white transition-colors group text-right min-h-[44px]"
                    >
                        <div className="text-right">
                            <div className="text-xs text-gray-500">Next</div>
                            <div className="font-medium max-w-[150px] truncate">{nextItem.title}</div>
                        </div>
                        <ChevronRight className="w-5 h-5 group-hover:translate-x-1 transition-transform" />
                    </button>
                )}
            </div>

            {/* Fullscreen lightbox: same photo, arrows, and slideshow controls, black
                edge-to-edge. Survives per-photo navigation because ?fs=1 rides in the
                URL; real browser fullscreen is layered on best-effort (see effect). */}
            {fullscreen && (
                <div ref={lightboxRef} className="fixed inset-0 z-50 bg-black overflow-hidden flex items-center justify-center group/fs">
                    <motion.img
                        key={item.id}
                        {...entranceProps}
                        src={fullImageUrl}
                        alt={item.title}
                        className="max-w-full max-h-full object-contain"
                    />

                    {prevItem && (
                        <button
                            onClick={() => navigate(`/media/${prevItem.id}${albumSuffix}`)}
                            aria-label="Previous photo"
                            className="absolute left-4 top-1/2 -translate-y-1/2 p-3 min-w-[48px] min-h-[48px] flex items-center justify-center rounded-full bg-black/50 text-white opacity-0 group-hover/fs:opacity-100 focus-visible:opacity-100 transition-opacity hover:bg-black/80"
                        >
                            <ChevronLeft className="w-7 h-7" />
                        </button>
                    )}
                    {nextItem && (
                        <button
                            onClick={() => navigate(`/media/${nextItem.id}${albumSuffix}`)}
                            aria-label="Next photo"
                            className="absolute right-4 top-1/2 -translate-y-1/2 p-3 min-w-[48px] min-h-[48px] flex items-center justify-center rounded-full bg-black/50 text-white opacity-0 group-hover/fs:opacity-100 focus-visible:opacity-100 transition-opacity hover:bg-black/80"
                        >
                            <ChevronRight className="w-7 h-7" />
                        </button>
                    )}

                    <div className="absolute top-4 right-4 flex gap-2">
                        <button
                            onClick={toggleSlideshow}
                            title={slideshow ? 'Pause slideshow' : 'Start slideshow'}
                            aria-label={slideshow ? 'Pause slideshow' : 'Start slideshow'}
                            className={`p-3 min-w-[48px] min-h-[48px] flex items-center justify-center rounded-full bg-black/50 text-white transition-opacity hover:bg-black/80 ${slideshow ? 'opacity-100' : 'opacity-0 group-hover/fs:opacity-100 focus-visible:opacity-100'}`}
                        >
                            {slideshow ? <Pause className="w-5 h-5" /> : <Play className="w-5 h-5 fill-current" />}
                        </button>
                        <button
                            onClick={exitFullscreen}
                            title="Exit fullscreen"
                            aria-label="Exit fullscreen"
                            className="p-3 min-w-[48px] min-h-[48px] flex items-center justify-center rounded-full bg-black/50 text-white opacity-0 group-hover/fs:opacity-100 focus-visible:opacity-100 transition-opacity hover:bg-black/80"
                        >
                            <X className="w-5 h-5" />
                        </button>
                    </div>

                    <div className="absolute bottom-4 left-1/2 -translate-x-1/2 text-white/80 text-sm bg-black/50 rounded-full px-4 py-1.5 opacity-0 group-hover/fs:opacity-100 transition-opacity">
                        {item.title}
                    </div>
                </div>
            )}
        </div >
    );
}
