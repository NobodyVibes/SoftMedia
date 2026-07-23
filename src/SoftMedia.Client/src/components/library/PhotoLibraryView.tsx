import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { motion } from 'framer-motion';
import {
    ArrowDownWideNarrow, ArrowLeft, ArrowUpNarrowWide, Camera, FolderOpen,
    Heart, Images, LayoutGrid, Rows3, RefreshCw, Search,
} from 'lucide-react';
import api from '../../services/api';
import { type MediaItem } from '../../types';
import { attachAuthToApiUrl, resolveCardPosterUrl } from '../../lib/mediaImageUrl';
import { useMediaTokenRefresh } from '../../hooks/useMediaTokenRefresh';
import { useDebounce } from '../../hooks/useDebounce';
import { Combobox } from '../ui/Combobox';
import { cn } from '../../lib/utils';

interface PhotoAlbum {
    key: string;
    name: string;
    photoCount: number;
    coverPhotoId: string;
    latestDate: string | null;
}

interface PhotoFacets {
    cameras: string[];
    years: number[];
}

const ALL_CAMERAS = 'All cameras';
const ALL_YEARS = 'All years';

/** Square photo tile with a heart overlay. The heart is a SIBLING button (never
 *  nested inside the open button — invalid HTML and an a11y trap). */
function PhotoTile({ photo, delay, onOpen, onToggleFavorite }: {
    photo: MediaItem;
    delay: number;
    onOpen: () => void;
    onToggleFavorite: () => void;
}) {
    return (
        <motion.div
            initial={{ opacity: 0 }}
            animate={{ opacity: 1 }}
            transition={{ duration: 0.2, delay }}
            className="group relative aspect-square rounded-lg overflow-hidden bg-white/5"
        >
            <img
                src={resolveCardPosterUrl(photo.posterPath) ?? undefined}
                alt={photo.title}
                loading="lazy"
                className="absolute inset-0 w-full h-full object-cover group-hover:scale-105 transition-transform duration-300"
            />
            <button
                onClick={onOpen}
                aria-label={photo.title}
                className="absolute inset-0 hover:ring-2 hover:ring-primary/70 focus-visible:ring-2 focus-visible:ring-primary rounded-lg transition-shadow"
            />
            <button
                onClick={onToggleFavorite}
                aria-label={photo.isFavorite ? 'Remove from favorites' : 'Add to favorites'}
                className={cn(
                    'absolute top-1.5 right-1.5 z-10 p-2.5 min-w-[40px] min-h-[40px] flex items-center justify-center rounded-full transition-all',
                    photo.isFavorite
                        ? 'text-red-500 bg-black/40 opacity-100'
                        : 'text-white bg-black/40 opacity-0 group-hover:opacity-100 focus-visible:opacity-100'
                )}
            >
                <Heart className={cn('w-4 h-4', photo.isFavorite && 'fill-current')} />
            </button>
        </motion.div>
    );
}

/**
 * Photo-library home: the user's folders ARE the albums ("2024/Italy" → "Italy").
 * Views: Albums (cards → per-album grid) and Timeline (everything, month headers).
 * The filter bar is photo-specialised (search, camera, year, favorites,
 * oldest/newest) — searching or filtering from the album grid searches ACROSS the
 * whole library; inside an album the same controls narrow that album.
 */
export default function PhotoLibraryView({ libraryId, libraryName, onRescan }: {
    libraryId: string;
    libraryName?: string;
    onRescan: () => void;
}) {
    // Cover/thumb URLs below embed the media token; re-render on rotation.
    useMediaTokenRefresh();
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const [album, setAlbum] = useState<PhotoAlbum | null>(null);
    const [view, setView] = useState<'albums' | 'timeline'>('albums');

    // Photo-specific filters
    const [search, setSearch] = useState('');
    const [camera, setCamera] = useState('');
    const [year, setYear] = useState<number | null>(null);
    const [favoritesOnly, setFavoritesOnly] = useState(false);
    const [sortDir, setSortDir] = useState<'asc' | 'desc'>('asc');
    const debouncedSearch = useDebounce(search, 400);
    const filtersActive = debouncedSearch.trim() !== '' || camera !== '' || year !== null || favoritesOnly;

    const { data: albums = [], isLoading: albumsLoading } = useQuery<PhotoAlbum[]>({
        queryKey: ['photoAlbums', libraryId],
        queryFn: async () => (await api.get<PhotoAlbum[]>(`/photos/albums?libraryId=${libraryId}`)).data,
    });

    const { data: facets } = useQuery<PhotoFacets>({
        queryKey: ['photoFacets', libraryId],
        queryFn: async () => (await api.get<PhotoFacets>(`/photos/filters?libraryId=${libraryId}`)).data,
    });

    // A library that is ONLY loose photos (single album) skips the album layer
    // entirely — one folder of pictures shouldn't need two clicks to see them.
    const openAlbum = album ?? (albums.length === 1 ? albums[0] : null);
    const timelineMode = view === 'timeline' && openAlbum === null;

    // Photo grid shows for: an open album, the timeline, or filter-driven search results.
    const showPhotoGrid = openAlbum !== null || timelineMode || filtersActive;

    const { data: photos = [], isLoading: photosLoading } = useQuery<MediaItem[]>({
        queryKey: ['photoAlbum', libraryId, openAlbum?.key ?? null, debouncedSearch, camera, year, favoritesOnly, sortDir, timelineMode],
        queryFn: async () => {
            const params = new URLSearchParams({ libraryId });
            // key present = album scope (incl. "" for the root album); absent = whole library.
            if (openAlbum !== null) params.set('key', openAlbum.key);
            if (debouncedSearch.trim() !== '') params.set('search', debouncedSearch.trim());
            if (camera !== '') params.set('camera', camera);
            if (year !== null) params.set('year', String(year));
            if (favoritesOnly) params.set('favorites', 'true');
            // Timeline reads newest-first regardless of the album-order toggle.
            params.set('sortDir', timelineMode ? 'desc' : sortDir);
            return (await api.get<MediaItem[]>(`/photos/albums/photos?${params}`)).data;
        },
        enabled: showPhotoGrid,
    });

    const favoriteMutation = useMutation({
        mutationFn: ({ id, isFavorite }: { id: string; isFavorite: boolean }) =>
            api.post(`/interaction/${id}/favorite`, { isFavorite }),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['photoAlbum', libraryId] }),
    });

    const albumDate = (iso: string | null) =>
        iso ? new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'short' }) : '';

    const monthLabel = (photo: MediaItem) => {
        const iso = photo.releaseDate ?? photo.dateAdded;
        return iso
            ? new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'long' })
            : 'Undated';
    };

    const clearFilters = () => { setSearch(''); setCamera(''); setYear(null); setFavoritesOnly(false); };

    const openPhoto = (photo: MediaItem) => navigate(openAlbum !== null
        ? `/media/${photo.id}?album=${encodeURIComponent(openAlbum.key)}`
        : `/media/${photo.id}`);

    // Timeline sections: consecutive run-length grouping by month label (the list is
    // already date-sorted server-side, so one pass suffices).
    const timelineSections: { label: string; photos: MediaItem[] }[] = [];
    if (timelineMode) {
        for (const photo of photos) {
            const label = monthLabel(photo);
            const last = timelineSections[timelineSections.length - 1];
            if (last && last.label === label) last.photos.push(photo);
            else timelineSections.push({ label, photos: [photo] });
        }
    }

    const photoGrid = (items: MediaItem[], baseIndex = 0) => (
        <div className="grid gap-2" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(160px, 1fr))' }}>
            {items.map((photo, i) => (
                <PhotoTile
                    key={photo.id}
                    photo={photo}
                    delay={Math.min((baseIndex + i) * 0.015, 0.3)}
                    onOpen={() => openPhoto(photo)}
                    onToggleFavorite={() => favoriteMutation.mutate({ id: photo.id, isFavorite: !photo.isFavorite })}
                />
            ))}
        </div>
    );

    return (
        <div className="flex-1 px-8 pt-6 pb-10">
            {/* Header: library name / album breadcrumb + view tabs + rescan */}
            <div className="flex items-center gap-3 mb-4">
                {openAlbum && albums.length > 1 && (
                    <button
                        onClick={() => { setAlbum(null); clearFilters(); }}
                        aria-label="Back to albums"
                        className="p-3 min-w-[44px] min-h-[44px] flex items-center justify-center rounded-full bg-white/5 hover:bg-white/10 focus-visible:bg-white/10 text-white transition-colors"
                    >
                        <ArrowLeft className="w-5 h-5" />
                    </button>
                )}
                <h1 className="text-2xl font-bold text-white flex items-center gap-3 flex-1">
                    {openAlbum ? (
                        <><Images className="w-6 h-6 text-primary" /> {openAlbum.name}
                            <span className="text-sm font-normal text-gray-500">{openAlbum.photoCount} photos</span></>
                    ) : filtersActive ? (
                        <><Search className="w-6 h-6 text-primary" /> Search results
                            <span className="text-sm font-normal text-gray-500">{photosLoading ? '…' : `${photos.length} photos`}</span></>
                    ) : timelineMode ? (
                        <><Rows3 className="w-6 h-6 text-primary" /> Timeline
                            <span className="text-sm font-normal text-gray-500">{photosLoading ? '…' : `${photos.length} photos`}</span></>
                    ) : (
                        <><FolderOpen className="w-6 h-6 text-primary" /> {libraryName ?? 'Photos'}
                            <span className="text-sm font-normal text-gray-500">{albums.length} albums</span></>
                    )}
                </h1>

                {/* Albums | Timeline toggle — only meaningful at the top level */}
                {openAlbum === null && albums.length > 1 && (
                    <div className="flex rounded-full bg-white/5 border border-white/10 p-1">
                        {([['albums', LayoutGrid, 'Albums'], ['timeline', Rows3, 'Timeline']] as const).map(([mode, Icon, label]) => (
                            <button
                                key={mode}
                                onClick={() => setView(mode)}
                                aria-label={`${label} view`}
                                className={cn(
                                    'px-4 py-2 min-h-[40px] rounded-full text-sm font-medium flex items-center gap-2 transition-colors',
                                    view === mode ? 'bg-primary/20 text-white' : 'text-gray-400 hover:text-white focus-visible:text-white'
                                )}
                            >
                                <Icon className="w-4 h-4" /> {label}
                            </button>
                        ))}
                    </div>
                )}

                <button
                    onClick={onRescan}
                    aria-label="Rescan library"
                    title="Rescan library"
                    className="p-3 min-w-[44px] min-h-[44px] flex items-center justify-center rounded-full bg-white/5 hover:bg-white/10 focus-visible:bg-white/10 text-white transition-colors"
                >
                    <RefreshCw className="w-5 h-5" />
                </button>
            </div>

            {/* Photo-specialised filter bar (house FilterBar styling) */}
            <div className="flex flex-col md:flex-row gap-3 items-center mb-6">
                <div className="relative w-full md:w-64">
                    <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-gray-400" />
                    <input
                        type="text"
                        value={search}
                        onChange={(e) => setSearch(e.target.value)}
                        placeholder={openAlbum ? `Search in ${openAlbum.name}...` : 'Search all photos...'}
                        className="w-full bg-white/5 border border-white/10 rounded-full pl-10 pr-4 py-2 text-sm text-white focus:outline-none focus:border-primary/50 transition-colors"
                    />
                </div>
                <div className="flex flex-wrap items-center gap-3 w-full md:w-auto">
                    <button
                        onClick={() => setFavoritesOnly(f => !f)}
                        aria-label={favoritesOnly ? 'Show all photos' : 'Show favorites only'}
                        className={cn(
                            'flex items-center gap-2 px-4 py-2 min-h-[40px] rounded-full text-sm font-medium border transition-colors',
                            favoritesOnly
                                ? 'bg-red-500/15 border-red-500/40 text-red-400'
                                : 'bg-white/5 border-white/10 text-gray-300 hover:text-white focus-visible:text-white'
                        )}
                    >
                        <Heart className={cn('w-4 h-4', favoritesOnly && 'fill-current')} /> Favorites
                    </button>
                    {(facets?.cameras.length ?? 0) > 0 && (
                        <div className="flex items-center gap-2">
                            <Camera className="w-4 h-4 text-gray-400" />
                            <Combobox
                                value={camera === '' ? ALL_CAMERAS : camera}
                                onChange={(val) => setCamera(val === ALL_CAMERAS ? '' : val)}
                                options={[ALL_CAMERAS, ...(facets?.cameras ?? [])]}
                                placeholder="Camera"
                                className="w-48"
                            />
                        </div>
                    )}
                    {(facets?.years.length ?? 0) > 1 && (
                        <Combobox
                            value={year === null ? ALL_YEARS : String(year)}
                            onChange={(val) => setYear(val === ALL_YEARS ? null : parseInt(val))}
                            options={[ALL_YEARS, ...(facets?.years ?? []).map(String)]}
                            placeholder="Year"
                            className="w-32"
                        />
                    )}
                    {!timelineMode && (
                        <button
                            onClick={() => setSortDir(d => d === 'asc' ? 'desc' : 'asc')}
                            aria-label={sortDir === 'asc' ? 'Sorted oldest first — switch to newest first' : 'Sorted newest first — switch to oldest first'}
                            title={sortDir === 'asc' ? 'Oldest first' : 'Newest first'}
                            className="p-2.5 min-w-[44px] min-h-[44px] flex items-center justify-center rounded-full bg-white/5 hover:bg-white/10 focus-visible:bg-white/10 text-gray-300 transition-colors"
                        >
                            {sortDir === 'asc' ? <ArrowUpNarrowWide className="w-4 h-4" /> : <ArrowDownWideNarrow className="w-4 h-4" />}
                        </button>
                    )}
                    {filtersActive && (
                        <button
                            onClick={clearFilters}
                            className="text-sm text-gray-400 hover:text-white focus-visible:text-white transition-colors min-h-[44px] px-2"
                        >
                            Clear
                        </button>
                    )}
                </div>
            </div>

            {(albumsLoading || (showPhotoGrid && photosLoading)) && (
                <div className="flex justify-center mt-16">
                    <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-primary" />
                </div>
            )}

            {/* Album grid */}
            {!showPhotoGrid && !albumsLoading && (
                albums.length === 0 ? (
                    <div className="text-center text-gray-500 mt-12">
                        <p className="text-xl">No photos yet.</p>
                        <p className="text-sm">Folders inside this library become albums after a scan.</p>
                    </div>
                ) : (
                    <div className="grid gap-6" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))' }}>
                        {albums.map((a, i) => (
                            <motion.button
                                key={a.key}
                                initial={{ opacity: 0, y: 12 }}
                                animate={{ opacity: 1, y: 0 }}
                                transition={{ duration: 0.25, delay: Math.min(i * 0.03, 0.3) }}
                                onClick={() => setAlbum(a)}
                                className="group relative aspect-square rounded-xl overflow-hidden bg-white/5 border border-white/10 text-left hover:border-white/25 focus-visible:border-white/25 transition-colors"
                            >
                                <img
                                    src={attachAuthToApiUrl(`/api/v1/photos/${a.coverPhotoId}/image?width=480`)}
                                    alt={a.name}
                                    loading="lazy"
                                    className="absolute inset-0 w-full h-full object-cover group-hover:scale-105 transition-transform duration-300"
                                />
                                <div className="absolute inset-x-0 bottom-0 bg-gradient-to-t from-black/85 via-black/40 to-transparent pt-10 pb-3 px-4">
                                    <div className="text-white font-semibold truncate">{a.name}</div>
                                    <div className="text-gray-400 text-xs">
                                        {a.photoCount} photos{a.latestDate ? ` · ${albumDate(a.latestDate)}` : ''}
                                    </div>
                                </div>
                            </motion.button>
                        ))}
                    </div>
                )
            )}

            {/* Timeline: month sections, newest first */}
            {timelineMode && !filtersActive && !photosLoading && (
                photos.length === 0 ? (
                    <div className="text-center text-gray-500 mt-12">
                        <p className="text-xl">No photos yet.</p>
                    </div>
                ) : (
                    <div className="space-y-8">
                        {(() => {
                            let baseIndex = 0;
                            return timelineSections.map((section) => {
                                const grid = (
                                    <div key={section.label}>
                                        <h2 className="sticky top-0 z-10 bg-background/90 backdrop-blur-sm text-lg font-semibold text-white py-2 mb-3">
                                            {section.label}
                                            <span className="text-sm font-normal text-gray-500 ml-3">{section.photos.length}</span>
                                        </h2>
                                        {photoGrid(section.photos, baseIndex)}
                                    </div>
                                );
                                baseIndex += section.photos.length;
                                return grid;
                            });
                        })()}
                    </div>
                )
            )}

            {/* Photo grid: an open album, or filter-driven search results (a filtered
                timeline collapses to the same flat results grid) */}
            {showPhotoGrid && (!timelineMode || filtersActive) && !photosLoading && (
                photos.length === 0 ? (
                    <div className="text-center text-gray-500 mt-12">
                        <p className="text-xl">No photos match.</p>
                        <p className="text-sm">Try different search terms or clear the filters.</p>
                    </div>
                ) : photoGrid(photos)
            )}
        </div>
    );
}
