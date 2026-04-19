import { useParams, useNavigate, Navigate } from 'react-router-dom';
import { useState, useEffect, useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
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
import { MediaType } from '../types';
import { useMediaHub } from '../hooks/useMediaHub';
import { Clock, User, Disc } from 'lucide-react';
import { formatDuration } from '../lib/utils';
import { Link } from 'react-router-dom';

export default function MediaDetailPage() {
    const { id } = useParams<{ id: string }>();

    // SignalR real-time updates - refreshes when images are cached
    useMediaHub({ mediaId: id });

    const { data: item, isLoading, error } = useQuery({
        queryKey: ['media', id],
        queryFn: async () => {
            const response = await api.get<MediaItem>(`/media/${id}`);
            return response.data;
        },
        enabled: !!id,
    });

    if (isLoading) {
        return <div className="flex justify-center items-center h-screen text-white">Loading...</div>;
    }

    if (error || !item) {
        return <div className="flex justify-center items-center h-screen text-red-500">Error loading media</div>;
    }

    return <MediaDetailPageContent item={item} />;
}

function MediaDetailPageContent({ item }: { item: MediaItem }) {
    const navigate = useNavigate();
    const { playTrack } = useAudioStore();

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
    const { data: albumTracks } = useQuery({
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
    const { data: artistTracks } = useQuery({
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
            playTrack(item);
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
            // For TV shows, fetch the next episode to watch based on watch history
            try {
                const response = await api.get<{
                    episodeId: string;
                    resumePosition: number;
                    isSeriesComplete: boolean;
                }>(`/series/${item.id}/next-episode`);

                const { episodeId, resumePosition } = response.data;

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

    // State for overriding quality info
    const [qualityItem, setQualityItem] = useState<MediaItem | null>(null);
    const [selectedEpisodeId, setSelectedEpisodeId] = useState<string | null>(null);

    // Reset when item changes
    useEffect(() => {
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
            qualityItem={qualityItem}
            backdropOverride={backdropOverride}
            customMetadata={customMetadata}
        >
            {renderContent()}
        </MediaDetailLayout>
    );
}
