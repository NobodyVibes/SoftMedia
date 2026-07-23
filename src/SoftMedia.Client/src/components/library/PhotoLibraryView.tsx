import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { motion } from 'framer-motion';
import { ArrowLeft, FolderOpen, Images, RefreshCw } from 'lucide-react';
import api from '../../services/api';
import { type MediaItem } from '../../types';
import { attachAuthToApiUrl, resolveCardPosterUrl } from '../../lib/mediaImageUrl';
import { useMediaTokenRefresh } from '../../hooks/useMediaTokenRefresh';

interface PhotoAlbum {
    key: string;
    name: string;
    photoCount: number;
    coverPhotoId: string;
    latestDate: string | null;
}

/**
 * Photo-library home: the user's folders ARE the albums ("2024/Italy" → "Italy").
 * Album cards → a square photo grid → the photo detail page (which scopes its
 * prev/next paging to the opened album via the ?album= param).
 */
export default function PhotoLibraryView({ libraryId, libraryName, onRescan }: {
    libraryId: string;
    libraryName?: string;
    onRescan: () => void;
}) {
    // Cover/thumb URLs below embed the media token; re-render on rotation.
    useMediaTokenRefresh();
    const navigate = useNavigate();
    const [album, setAlbum] = useState<PhotoAlbum | null>(null);

    const { data: albums = [], isLoading: albumsLoading } = useQuery<PhotoAlbum[]>({
        queryKey: ['photoAlbums', libraryId],
        queryFn: async () => (await api.get<PhotoAlbum[]>(`/photos/albums?libraryId=${libraryId}`)).data,
    });

    // A library that is ONLY loose photos (single album) skips the album layer
    // entirely — one folder of pictures shouldn't need two clicks to see them.
    // Derived, not setState-during-render: `album` stays null until a real click.
    const openAlbum = album ?? (albums.length === 1 ? albums[0] : null);

    const { data: photos = [], isLoading: photosLoading } = useQuery<MediaItem[]>({
        queryKey: ['photoAlbum', libraryId, openAlbum?.key ?? null],
        queryFn: async () => (await api.get<MediaItem[]>(
            `/photos/albums/photos?libraryId=${libraryId}&key=${encodeURIComponent(openAlbum!.key)}`)).data,
        enabled: openAlbum !== null,
    });

    const albumDate = (iso: string | null) =>
        iso ? new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'short' }) : '';

    return (
        <div className="flex-1 px-8 pt-6 pb-10">
            {/* Header: library name / album breadcrumb + rescan */}
            <div className="flex items-center gap-3 mb-6">
                {openAlbum && albums.length > 1 && (
                    <button
                        onClick={() => setAlbum(null)}
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
                    ) : (
                        <><FolderOpen className="w-6 h-6 text-primary" /> {libraryName ?? 'Photos'}
                            <span className="text-sm font-normal text-gray-500">{albums.length} albums</span></>
                    )}
                </h1>
                <button
                    onClick={onRescan}
                    aria-label="Rescan library"
                    title="Rescan library"
                    className="p-3 min-w-[44px] min-h-[44px] flex items-center justify-center rounded-full bg-white/5 hover:bg-white/10 focus-visible:bg-white/10 text-white transition-colors"
                >
                    <RefreshCw className="w-5 h-5" />
                </button>
            </div>

            {(albumsLoading || (openAlbum && photosLoading)) && (
                <div className="flex justify-center mt-16">
                    <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-primary" />
                </div>
            )}

            {/* Album grid */}
            {!openAlbum && !albumsLoading && (
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

            {/* Photo grid inside an album */}
            {openAlbum && !photosLoading && (
                <div className="grid gap-2" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(160px, 1fr))' }}>
                    {photos.map((photo, i) => (
                        <motion.button
                            key={photo.id}
                            initial={{ opacity: 0 }}
                            animate={{ opacity: 1 }}
                            transition={{ duration: 0.2, delay: Math.min(i * 0.015, 0.3) }}
                            onClick={() => navigate(`/media/${photo.id}?album=${encodeURIComponent(openAlbum.key)}`)}
                            aria-label={photo.title}
                            className="group relative aspect-square rounded-lg overflow-hidden bg-white/5 hover:ring-2 hover:ring-primary/70 focus-visible:ring-2 focus-visible:ring-primary transition-shadow"
                        >
                            <img
                                src={resolveCardPosterUrl(photo.posterPath) ?? undefined}
                                alt={photo.title}
                                loading="lazy"
                                className="absolute inset-0 w-full h-full object-cover group-hover:scale-105 transition-transform duration-300"
                            />
                        </motion.button>
                    ))}
                </div>
            )}
        </div>
    );
}
