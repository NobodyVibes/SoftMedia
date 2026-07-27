import { useParams, useNavigate, Navigate } from 'react-router-dom';
import { useState, useEffect, useMemo, useRef } from 'react';
import { useQuery } from '@tanstack/react-query';
import { isAxiosError } from 'axios';
import api from '../services/api';
import { type MediaItem } from '../types';
import MediaDetailLayout from '../components/layout/MediaDetailLayout';
import MovieDetailView from '../components/details/MovieDetailView';
import TVDetailView from '../components/details/TVDetailView';
import MusicDetailView from '../components/details/MusicDetailView';
import ArtistDetailView from '../components/details/ArtistDetailView';
import AlbumDetailView from '../components/details/AlbumDetailView';
import BookDetailView from '../components/details/BookDetailView';
import ComicSeriesDetailView from '../components/details/ComicSeriesDetailView';
import GameDetailView from '../components/details/GameDetailView';
import PhotoDetailView from '../components/details/PhotoDetailView';
import { useAudioStore } from '../store/audioStore';
import { useAuthStore } from '../store/authStore';
import { FixMatchCard } from '../components/admin/FixMatchCard';
import { MediaType } from '../types';
import { useMediaHub } from '../hooks/useMediaHub';
import { Clock, User, Disc, ArrowLeft, RefreshCw } from 'lucide-react';
import { formatDuration } from '../lib/utils';
import { bookReadLabel } from '../lib/bookReadLabel';
import { getProgress as getBookProgress } from '../services/bookService';
import { BookOpen } from 'lucide-react';
import { Link } from 'react-router-dom';

export default function MediaDetailPage() {
    const { id } = useParams<{ id: string }>();
    const navigate = useNavigate();

    // SignalR real-time updates - refreshes when images are cached
    useMediaHub({ mediaId: id });

    const { data: item, isLoading, error, refetch } = useQuery({
        queryKey: ['media', id],
        queryFn: async () => {
            const response = await api.get<MediaItem>(`/media/${id}`);
            return response.data;
        },
        enabled: !!id,
        // Keep the previous item rendered while the next loads: detail→detail
        // navigation (photo paging/slideshows, collection hops) would otherwise
        // unmount the page into a "Loading..." flash — which also destroys any
        // in-flight photo crossfade.
        placeholderData: (previous: MediaItem | undefined) => previous,
    });

    if (isLoading) {
        return <div className="flex justify-center items-center h-screen text-white">Loading...</div>;
    }

    if (error || !item) {
        // SR-WI-052 batch (detail-page slice): a 404 means the item is gone —
        // retrying can never succeed, so offer the way out instead. Everything
        // else (network blip, 500) gets a real Retry that refetches in place.
        const notFound = isAxiosError(error) && error.response?.status === 404;
        return (
            <div className="min-h-screen flex flex-col items-center justify-center text-center px-6 gap-4">
                <h1 className="text-2xl font-bold text-white">
                    {notFound ? 'This item no longer exists' : 'Could not load this item'}
                </h1>
                <p className="text-gray-400 max-w-sm">
                    {notFound
                        ? 'It may have been removed from the library or moved during a scan.'
                        : 'Something went wrong while loading. Check that your server is reachable and try again.'}
                </p>
                {notFound ? (
                    <button
                        type="button"
                        // The item is gone, so no parent page can be derived from it —
                        // home is the one destination that always exists. (Never browser
                        // history: the previous entry may be the player for this same
                        // now-deleted item.)
                        onClick={() => navigate('/')}
                        className="inline-flex items-center gap-2 px-5 py-2.5 min-h-[44px] rounded-lg bg-white/10 hover:bg-white/15 text-white font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                    >
                        <ArrowLeft className="w-4 h-4" aria-hidden="true" />
                        Go home
                    </button>
                ) : (
                    <button
                        type="button"
                        onClick={() => refetch()}
                        className="inline-flex items-center gap-2 px-5 py-2.5 min-h-[44px] rounded-lg text-white font-medium transition-opacity hover:opacity-90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                        style={{ background: 'linear-gradient(135deg, #007AFF, #8A2BE2)' }}
                    >
                        <RefreshCw className="w-4 h-4" aria-hidden="true" />
                        Retry
                    </button>
                )}
            </div>
        );
    }

    return <MediaDetailPageContent item={item} />;
}

function MediaDetailPageContent({ item }: { item: MediaItem }) {
    const navigate = useNavigate();
    const { playTrack, playPlaylist } = useAudioStore();
    const isAdmin = useAuthStore((s) => s.user?.role === 'Admin');

    // Fetch library to get type
    const { data: library } = useQuery({
        queryKey: ['library', item.libraryId],
        queryFn: async () => {
            const response = await api.get(`/libraries/${item.libraryId}`);
            return response.data;
        },
        enabled: !!item.libraryId
    });

    const type = library?.type;

    // Fetch albums for Artist background (random cover art)
    const { data: artistAlbums } = useQuery({
        queryKey: ['artist', item.id, 'albums'],
        queryFn: async () => {
            if (item.type !== MediaType.Artist) return null;
            const response = await api.get<MediaItem[]>(`/libraries/artists/${item.id}/albums`);
            return response.data;
        },
        enabled: item.type === MediaType.Artist,
        staleTime: 1000 * 60 * 5 // Cache for 5 minutes
    });

    // Fetch tracks for Album (for total duration and track count)
    const { data: albumTracks, isLoading: albumTracksLoading } = useQuery({
        queryKey: ['album', item.id, 'tracks'],
        queryFn: async () => {
            if (item.type !== MediaType.Album) return null;
            const response = await api.get<MediaItem[]>(`/libraries/albums/${item.id}/tracks`);
            return response.data;
        },
        enabled: item.type === MediaType.Album,
        staleTime: 1000 * 60 * 5
    });

    // Fetch tracks for all of an Artist's albums (for stats)
    const { data: artistTracks, isLoading: artistTracksLoading } = useQuery({
        queryKey: ['artist', item.id, 'all-tracks'],
        queryFn: async () => {
            if (item.type !== MediaType.Artist || !artistAlbums || artistAlbums.length === 0) return [];
            const allTrackPromises = artistAlbums.map(album =>
                api.get<MediaItem[]>(`/libraries/albums/${album.id}/tracks`)
            );
            const responses = await Promise.all(allTrackPromises);
            return responses.flatMap(r => r.data);
        },
        enabled: item.type === MediaType.Artist && !!artistAlbums && artistAlbums.length > 0,
        staleTime: 1000 * 60 * 5
    });

    // SR-WI-053 — resume position for the split Play control.
    //
    // Source: the detail payload (GET /media/{id}) does NOT carry PlaybackPosition
    // (MediaItemDto.FromMediaItem maps the interaction's rating/favorite/watched/
    // watchlist flags but not the position), so the page queries the existing
    // per-item progress endpoint the VideoPlayer already uses:
    //   GET /interaction/{id}/progress → { position, isWatched, ... }
    // For a Series the position lives on an EPISODE, so we use the next-episode
    // endpoint handlePlay already relies on — prefetching it here also removes
    // the click-time fetch latency from the Play button.
    const isResumableVideo = item.type === MediaType.Movie || item.type === MediaType.Episode;

    const { data: progress } = useQuery({
        queryKey: ['media', item.id, 'progress'],
        queryFn: async () => {
            const response = await api.get<{ position: number }>(`/interaction/${item.id}/progress`);
            return response.data;
        },
        enabled: isResumableVideo,
    });

    // Books have no "Play" — the primary button IS the reader entry point (there is no
    // second read link in the body). Same query key BookDetailView used, so this is the
    // one fetch that labels the button.
    const isBook = item.type === MediaType.Book;
    const { data: bookProgress } = useQuery({
        queryKey: ['book-progress', item.id],
        queryFn: () => getBookProgress(item.id),
        staleTime: 30_000,
        enabled: isBook,
    });

    const { data: nextEpisode, isPending: nextEpisodePending } = useQuery({
        queryKey: ['series', item.id, 'next-episode'],
        queryFn: async () => {
            const response = await api.get<{
                episodeId: string;
                seasonNumber: number;
                episodeNumber: number;
                title: string;
                resumePosition: number;
                isSeriesComplete: boolean;
            }>(`/series/${item.id}/next-episode`);
            return response.data;
        },
        enabled: item.type === MediaType.Series,
    });

    // Names the episode "Resume from H:MM" would play. Only when there really is
    // a position to resume — with none, the button says Play and there is nothing
    // to disambiguate.
    const resumeCaption = useMemo(() => {
        if (item.type !== MediaType.Series || !nextEpisode || nextEpisode.resumePosition <= 0) return undefined;
        const slug = `S${nextEpisode.seasonNumber} E${nextEpisode.episodeNumber}`;
        return nextEpisode.title ? `${slug} · ${nextEpisode.title}` : slug;
    }, [item.type, nextEpisode]);

    const resumePositionSeconds = useMemo(() => {
        if (isResumableVideo) {
            const pos = progress?.position ?? 0;
            if (pos <= 0) return null;
            // Mirror the VideoPlayer's completion rule (server MediaCompletionHelper,
            // >=95% or within the last 5s = finished): the player would restart such
            // a position from the top anyway, so offering "Resume" would be a lie.
            const dur = item.durationSeconds ?? 0;
            if (dur > 0 && pos >= Math.min(dur - 5, dur * 0.95)) return null;
            return pos;
        }
        if (item.type === MediaType.Series) {
            const pos = nextEpisode?.resumePosition ?? 0;
            return pos > 0 ? pos : null;
        }
        return null;
    }, [isResumableVideo, progress, item.type, item.durationSeconds, nextEpisode]);

    const backdropOverride = useMemo(() => {
        // For Albums, use the album cover itself as backdrop
        if (item.type === MediaType.Album && item.posterPath) {
            return item.posterPath;
        }

        // For Artists, pick a random album cover
        if (item.type === MediaType.Artist && artistAlbums && artistAlbums.length > 0) {
            // Filter albums that have a poster path
            const albumsWithCovers = artistAlbums.filter(a => a.posterPath);
            if (albumsWithCovers.length > 0) {
                const randomIndex = Math.floor(Math.random() * albumsWithCovers.length);
                return albumsWithCovers[randomIndex].posterPath;
            }
        }
        return null;
    }, [item.type, item.posterPath, artistAlbums]);

    const customMetadata = useMemo(() => {
        if (item.type === MediaType.Album && albumTracks) {
            const artistName = (item.metadata?.artist as string) || albumTracks?.[0]?.metadata?.artist as string;
            const totalDuration = albumTracks.reduce((acc, t) => acc + (t.durationSeconds || 0), 0);

            return (
                <div className="flex items-center gap-4 text-gray-300">
                    {item.artistId && artistName && (
                        <Link
                            to={`/media/${item.artistId}`}
                            className="flex items-center gap-2 hover:text-white transition-colors group"
                        >
                            <User className="w-4 h-4 text-gray-500 group-hover:text-primary" />
                            <span className="font-medium group-hover:underline">{artistName}</span>
                        </Link>
                    )}
                    <div className="flex items-center gap-4">
                        <span className="text-gray-600">•</span>
                        <span>{albumTracks.length} tracks</span>
                        <span className="text-gray-600">•</span>
                        <div className="flex items-center gap-1.5 font-medium text-gray-300">
                            <Clock className="w-4 h-4 text-gray-500" />
                            <span>{formatDuration(totalDuration)}</span>
                        </div>
                    </div>
                </div>
            );
        }

        if (item.type === MediaType.Artist && artistAlbums) {
            const albumCount = artistAlbums.length;
            const trackCount = artistTracks?.length || 0;

            return (
                <div className="flex items-center gap-4 text-gray-300">
                    <div className="flex items-center gap-2">
                        <Disc className="w-4 h-4 text-gray-500" />
                        <span>{albumCount} {albumCount === 1 ? 'album' : 'albums'}</span>
                    </div>
                    {trackCount > 0 && (
                        <>
                            <span className="text-gray-600">•</span>
                            <span>{trackCount} tracks</span>
                        </>
                    )}
                </div>
            );
        }
        return null;
    }, [item, albumTracks, artistAlbums, artistTracks]);

    const handlePlay = async () => {
        if (type === 'Music') {
            // Albums/Artists are NOT streamable themselves — playTrack(album)
            // requested /stream/{albumId}, which 404s and leaves the player bar
            // as a silent zombie (same class MediaCard.handlePlay already fixed).
            // The layout hides Play for these types today; this guard keeps the
            // handler correct if that ever changes.
            if (item.type === MediaType.Album) {
                if (albumTracks && albumTracks.length > 0) playPlaylist(albumTracks);
            } else if (item.type === MediaType.Artist) {
                if (artistTracks && artistTracks.length > 0) playPlaylist(artistTracks);
            } else {
                playTrack(item);
            }
        } else if (item.type === MediaType.ComicSeries) {
            // Open the first issue in the reader (chronological by issue number).
            try {
                const res = await api.get<MediaItem[]>(`/libraries/comics/${item.id}/issues`);
                const first = res.data?.[0];
                if (first) {
                    navigate(`/read/${first.id}`);
                }
            } catch (err) {
                console.error('Failed to fetch comic issues:', err);
            }
        } else if (type === 'Book') {
            navigate(`/read/${item.id}`);
        } else if (type === 'TV') {
            // For TV shows, play the next episode to watch based on watch history.
            // The page prefetches it for the Resume label; fall back to a fetch
            // when that query hasn't resolved (or errored) by click time.
            try {
                const { episodeId, resumePosition } = nextEpisode ?? (await api.get<{
                    episodeId: string;
                    resumePosition: number;
                    isSeriesComplete: boolean;
                }>(`/series/${item.id}/next-episode`)).data;

                // Navigate to the episode with resume position
                if (resumePosition > 0) {
                    navigate(`/play/${episodeId}?start=${resumePosition}`);
                } else {
                    navigate(`/play/${episodeId}`);
                }
            } catch (error) {
                console.error('Failed to fetch next episode:', error);
                navigate(`/play/${item.id}`);
            }
        } else {
            navigate(`/play/${item.id}`);
        }
    };

    // SR-WI-053 — the split control's secondary action: same target as Play,
    // but with ?start=0 (the NextEpisodeOverlay restart convention VideoPlayer
    // already honours by skipping its resume-position fetch).
    const handlePlayFromBeginning = async () => {
        if (item.type === MediaType.Series) {
            try {
                const episodeId = nextEpisode?.episodeId
                    ?? (await api.get<{ episodeId: string }>(`/series/${item.id}/next-episode`)).data.episodeId;
                navigate(`/play/${episodeId}?start=0`);
            } catch (error) {
                console.error('Failed to fetch next episode:', error);
                navigate(`/play/${item.id}?start=0`);
            }
            return;
        }
        navigate(`/play/${item.id}?start=0`);
    };

    // SR-WI-050 (CLI-L) — pressing Play on an Album/Artist before its track list
    // resolves was a silent no-op (handlePlay guards on the data being present).
    // Surface that window as a disabled-with-spinner Play instead.
    const playPending =
        (item.type === MediaType.Album && albumTracksLoading) ||
        (item.type === MediaType.Artist && artistTracksLoading);

    // State for overriding quality info
    const [qualityItem, setQualityItem] = useState<MediaItem | null>(null);
    const [selectedEpisodeId, setSelectedEpisodeId] = useState<string | null>(null);

    // Reset when the page swaps to a DIFFERENT item (this component stays mounted
    // across detail→detail navigation). The mount run is deliberately skipped:
    // child effects fire before the parent's, so with the queries already cached
    // TVDetailView has reported its resume episode by the time this runs, and an
    // unconditional reset would wipe that selection right back out.
    const previousItemIdRef = useRef(item.id);
    useEffect(() => {
        if (previousItemIdRef.current === item.id) return;
        previousItemIdRef.current = item.id;
        setQualityItem(null);
        setSelectedEpisodeId(null);
    }, [item.id]);

    const handleEpisodeSelect = (episode: MediaItem) => {
        setQualityItem(episode);
        setSelectedEpisodeId(episode.id);
    };

    const handleDefaultQualityFound = (episode: MediaItem) => {
        setQualityItem(prev => prev ? prev : episode);
    };

    const renderContent = () => {
        if (!type) return null;

        if (item.type === MediaType.Artist) return <ArtistDetailView item={item} />;
        if (item.type === MediaType.Album) return <AlbumDetailView item={item} />;

        // Comic hierarchy: series shows the issue list; individual issues go straight to the reader.
        if (item.type === MediaType.ComicSeries) return <ComicSeriesDetailView item={item} />;
        if (item.type === MediaType.ComicIssue) {
            return <Navigate to={`/read/${item.id}`} replace />;
        }

        if (item.type === MediaType.Audio || item.type === MediaType.Track) {
            if (item.albumId) {
                return <Navigate to={`/media/${item.albumId}?highlight=${item.id}`} replace />;
            }
            return <MusicDetailView item={item} />;
        }

        switch (type) {
            case 'Movie': return <MovieDetailView item={item} />;
            case 'TV':
                return (
                    <TVDetailView
                        item={item}
                        selectedEpisodeId={selectedEpisodeId}
                        resumeEpisodeId={nextEpisode?.episodeId ?? null}
                        resumeEpisodePending={item.type === MediaType.Series && nextEpisodePending}
                        resumeHasPosition={(nextEpisode?.resumePosition ?? 0) > 0}
                        onEpisodeSelect={handleEpisodeSelect}
                        onDefaultQualityItemFound={handleDefaultQualityFound}
                    />
                );
            case 'Music': return <MusicDetailView item={item} />;
            case 'Book': return <BookDetailView item={item} />;
            case 'Game': return <GameDetailView item={item} />;
            case 'Photo': return <PhotoDetailView item={item} />;
            default: return null;
        }
    };

    return (
        <MediaDetailLayout
            item={item}
            onPlay={handlePlay}
            onPlayFromBeginning={handlePlayFromBeginning}
            resumePositionSeconds={resumePositionSeconds}
            resumeCaption={resumeCaption}
            playPending={playPending}
            playLabel={isBook ? bookReadLabel(item.container, bookProgress) : undefined}
            playIcon={isBook ? <BookOpen className="w-6 h-6" aria-hidden="true" /> : undefined}
            qualityItem={qualityItem}
            backdropOverride={backdropOverride}
            customMetadata={customMetadata}
            actionSlot={isAdmin ? <FixMatchCard item={item} /> : undefined}
        >
            {renderContent()}
        </MediaDetailLayout>
    );
}
