import { useEffect } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import api from '../../services/api';
import { type MediaItem, type PagedResult } from '../../types';
import { attachAuthToApiUrl } from '../../lib/mediaImageUrl';
import { buildPhotoNavSuffix } from '../../lib/photoNav';
import { Camera, MapPin, Aperture, Clock, ChevronLeft, ChevronRight, Maximize2, Pause, Play } from 'lucide-react';

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

    // Navigation suffix carries BOTH contexts: which album we're paging within, and
    // whether the slideshow keeps rolling across the navigation.
    const albumSuffix = buildPhotoNavSuffix(albumKey, slideshow);

    const toggleSlideshow = () => {
        const next = new URLSearchParams(searchParams);
        if (slideshow) next.delete('slideshow');
        else next.set('slideshow', '1');
        setSearchParams(next, { replace: true });
    };

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
        };
        window.addEventListener('keydown', onKeyDown);
        return () => window.removeEventListener('keydown', onKeyDown);
    }, [prevItem, nextItem, navigate, albumSuffix]);

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
            const cleared = new URLSearchParams(searchParams);
            cleared.delete('slideshow');
            setSearchParams(cleared, { replace: true });
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

    return (
        <div className="space-y-8">
            {/* The photo IS the content — show it full-width, letterboxed. */}
            <div className="relative group rounded-xl overflow-hidden bg-black/60 border border-white/10">
                <img
                    src={fullImageUrl}
                    alt={item.title}
                    className="w-full max-h-[75vh] object-contain"
                />
                <div className="absolute top-3 right-3 flex gap-2">
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
        </div >
    );
}
