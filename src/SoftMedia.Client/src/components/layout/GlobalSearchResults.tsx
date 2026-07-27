import { useNavigate } from 'react-router-dom';
import { motion } from 'framer-motion';
import { Play, Film, Tv, Music, BookOpen, Gamepad2, Image, ListMusic, Sparkles, Lock, Globe, LibraryBig } from 'lucide-react';
import type { GlobalSearchResult } from '../../services/searchService';
import type { PlaylistSummary } from '../../services/playlistService';
import type { Library } from '../../types';
import { PlaylistCover } from '../playlists/PlaylistCover';
import { buildSearchSections } from '../../lib/searchRanking';
import { MediaType, type MediaItem } from '../../types';
import { useAudioStore } from '../../store/audioStore';
import { resolveCardPosterUrl } from '../../lib/mediaImageUrl';
import { useMediaTokenRefresh } from '../../hooks/useMediaTokenRefresh';

/** Context line that disambiguates duplicate titles across result types. */
function subtitleFor(item: MediaItem): string | null {
    const artist = item.metadata?.artist as string | undefined;
    const album = item.metadata?.album as string | undefined;
    const series = item.metadata?.seriesTitle as string | undefined;
    if (item.type === MediaType.Audio || item.type === MediaType.Track) {
        const parts = [artist, album].filter(Boolean);
        return parts.length > 0 ? parts.join(' — ') : null;
    }
    if (item.type === MediaType.Episode) {
        const se = item.seasonNumber != null && item.episodeNumber != null
            ? `S${item.seasonNumber} · E${item.episodeNumber}`
            : null;
        return [series, se].filter(Boolean).join(' — ') || null;
    }
    if (item.type === MediaType.Album && artist) return artist;
    return null;
}

interface GlobalSearchResultsProps {
    results: GlobalSearchResult[];
    /**
     * Playlist hits. They arrive separately because a playlist is not a media
     * item and belongs to no library (see playlistService.search) — but they no
     * longer render pinned first: buildSearchSections places every section by
     * match quality.
     */
    playlists?: PlaylistSummary[];
    /**
     * The user's (ACL-filtered) library list, so a library can be found by its
     * NAME — previously a library only appeared via the coincidence of
     * containing matching items.
     */
    libraries?: Library[];
    /** The active query; sections need it to score playlist/library hits. */
    query: string;
    isLoading: boolean;
    onClose: () => void;
}

const libraryIcons: Record<string, React.ReactNode> = {
    Movie: <Film size={14} />,
    TV: <Tv size={14} />,
    Music: <Music size={14} />,
    Book: <BookOpen size={14} />,
    Game: <Gamepad2 size={14} />,
    Photo: <Image size={14} />,
};

export default function GlobalSearchResults({ results, playlists = [], libraries = [], query, isLoading, onClose }: GlobalSearchResultsProps) {
    // Media URLs below embed the media token; re-render when it rotates so a
    // stale token can't leave the artwork permanently broken.
    useMediaTokenRefresh();
    const navigate = useNavigate();
    const playTrack = useAudioStore((s) => s.playTrack);

    const handlePlay = (e: React.MouseEvent, item: MediaItem) => {
        e.preventDefault();
        e.stopPropagation();
        onClose();
        // `/player/...` was a dead route (the router only knows /play/:id, and the
        // catch-all dumped every search-play click on the home page). Only video
        // items belong in the video player; tracks (searchable since R-WI-017) play
        // in the audio player; everything else opens its detail page, which owns
        // the right play/read affordance.
        if (item.type === MediaType.Movie || item.type === MediaType.Episode) {
            navigate(`/play/${item.id}`);
        } else if (item.type === MediaType.Audio || item.type === MediaType.Track) {
            playTrack(item);
        } else if (item.type === MediaType.ComicIssue) {
            // B-06: an issue's "detail page" is the reader — /media/{issueId}
            // renders an empty series-shaped shell.
            navigate(`/read/${item.id}`);
        } else {
            navigate(`/media/${item.id}`);
        }
    };

    const handleItemClick = (item: MediaItem) => {
        onClose();
        // Episodes have no working detail page — MediaDetailPage would render a
        // series-shaped empty shell for an episode id. Their series page is the
        // right destination (tracks are fine: /media/{trackId} redirects to the
        // album with the track highlighted).
        if (item.type === MediaType.Episode && item.seriesId) {
            navigate(`/media/${item.seriesId}`);
        } else if (item.type === MediaType.ComicIssue) {
            navigate(`/read/${item.id}`); // B-06: issues open in the reader
        } else {
            navigate(`/media/${item.id}`);
        }
    };

    if (isLoading) {
        return (
            <motion.div
                initial={{ opacity: 0, y: -10 }}
                animate={{ opacity: 1, y: 0 }}
                exit={{ opacity: 0, y: -10 }}
                className="absolute top-full left-0 right-0 mt-2 bg-[#1a1a1a] border border-white/10 rounded-xl shadow-2xl overflow-hidden z-50"
            >
                <div className="p-4 text-center text-gray-400">
                    <div className="animate-pulse">Searching...</div>
                </div>
            </motion.div>
        );
    }

    // One relevance scale across all three sources — media groups, playlists,
    // library-name hits — so placement reflects match quality, not result type.
    const sections = buildSearchSections({ query, mediaGroups: results, playlists, libraries });

    // B-07: a zero-hit query used to render nothing at all — the dropdown just
    // vanished, indistinguishable from "search is broken". TopBar only mounts
    // this component for a ≥2-char query, so an explicit empty state is safe.
    if (sections.length === 0) {
        return (
            <motion.div
                initial={{ opacity: 0, y: -10 }}
                animate={{ opacity: 1, y: 0 }}
                exit={{ opacity: 0, y: -10 }}
                className="absolute top-full left-0 right-0 mt-2 bg-[#1a1a1a] border border-white/10 rounded-xl shadow-2xl overflow-hidden z-50"
            >
                <div className="p-4 text-center text-gray-400 text-sm">No results found</div>
            </motion.div>
        );
    }

    return (
        <motion.div
            initial={{ opacity: 0, y: -10 }}
            animate={{ opacity: 1, y: 0 }}
            exit={{ opacity: 0, y: -10 }}
            transition={{ duration: 0.2 }}
            className="absolute top-full left-0 right-0 mt-2 bg-[#1a1a1a] border border-white/10 rounded-xl shadow-2xl overflow-hidden z-50 max-h-[70vh] overflow-y-auto"
        >
            {/* Sections in relevance order — a playlist named "Testing" no longer
                outranks a movie titled exactly "Test". */}
            {sections.map((section) => {
                if (section.kind === 'playlists') {
                    return (
                <div key="playlists">
                    <div className="px-4 py-2 bg-gradient-to-r from-primary/10 to-secondary/10 border-b border-white/5 flex items-center gap-2">
                        <span className="text-primary"><ListMusic size={14} /></span>
                        <span className="text-xs font-semibold text-gray-300 uppercase tracking-wider">
                            Playlists
                        </span>
                    </div>
                    <div className="divide-y divide-white/5">
                        {section.playlists.map((playlist) => (
                            <div
                                key={playlist.id}
                                role="button"
                                tabIndex={0}
                                onClick={() => { onClose(); navigate(`/playlists/${playlist.id}`); }}
                                onKeyDown={(e) => {
                                    if (e.key === 'Enter' || e.key === ' ') {
                                        e.preventDefault();
                                        onClose();
                                        navigate(`/playlists/${playlist.id}`);
                                    }
                                }}
                                className="w-full px-4 py-3 flex items-center gap-3 hover:bg-white/5 transition-colors group text-left cursor-pointer"
                            >
                                {/* Square, matching the playlist cards; the 10x14 poster
                                    box used for media would letterbox the mosaic. */}
                                <PlaylistCover
                                    coverPaths={playlist.coverImagePaths}
                                    className="w-10 h-10 rounded overflow-hidden flex-shrink-0"
                                    iconClassName="w-1/2 h-1/2"
                                />
                                <div className="flex-1 min-w-0">
                                    <p className="text-sm font-medium text-white truncate group-hover:text-primary transition-colors flex items-center gap-1.5">
                                        {playlist.kind === 'Smart' && (
                                            <Sparkles className="w-3 h-3 text-primary shrink-0" aria-label="Automatic playlist" />
                                        )}
                                        <span className="truncate">{playlist.name}</span>
                                    </p>
                                    <p className="text-xs text-gray-400 truncate flex items-center gap-1.5">
                                        {playlist.isPublic
                                            ? <Globe className="w-3 h-3 shrink-0" />
                                            : <Lock className="w-3 h-3 shrink-0" />}
                                        {playlist.isOwner
                                            ? (playlist.kind === 'Smart' ? 'Automatic playlist' : 'Your playlist')
                                            : `Shared by ${playlist.ownerUsername}`}
                                    </p>
                                </div>
                            </div>
                        ))}
                    </div>
                </div>
                    );
                }

                if (section.kind === 'libraries') {
                    return (
                <div key="libraries">
                    <div className="px-4 py-2 bg-gradient-to-r from-primary/10 to-secondary/10 border-b border-white/5 flex items-center gap-2">
                        <span className="text-primary"><LibraryBig size={14} /></span>
                        <span className="text-xs font-semibold text-gray-300 uppercase tracking-wider">
                            Libraries
                        </span>
                    </div>
                    <div className="divide-y divide-white/5">
                        {section.libraries.map((library) => (
                            <div
                                key={library.id}
                                role="button"
                                tabIndex={0}
                                onClick={() => { onClose(); navigate(`/libraries/${library.id}`); }}
                                onKeyDown={(e) => {
                                    if (e.key === 'Enter' || e.key === ' ') {
                                        e.preventDefault();
                                        onClose();
                                        navigate(`/libraries/${library.id}`);
                                    }
                                }}
                                className="w-full px-4 py-3 flex items-center gap-3 hover:bg-white/5 transition-colors group text-left cursor-pointer"
                            >
                                <div className="w-10 h-10 rounded bg-gradient-to-br from-primary/20 to-secondary/20 flex items-center justify-center flex-shrink-0 text-primary">
                                    {libraryIcons[library.type] || <LibraryBig size={16} />}
                                </div>
                                <div className="flex-1 min-w-0">
                                    <p className="text-sm font-medium text-white truncate group-hover:text-primary transition-colors">
                                        {library.name}
                                    </p>
                                    <p className="text-xs text-gray-400 truncate">
                                        {library.type} library
                                    </p>
                                </div>
                            </div>
                        ))}
                    </div>
                </div>
                    );
                }

                const group = section.group;
                return (
                <div key={group.libraryId}>
                    {/* Library Header */}
                    <div className="px-4 py-2 bg-gradient-to-r from-primary/10 to-secondary/10 border-b border-white/5 flex items-center gap-2">
                        <span className="text-primary">
                            {libraryIcons[group.libraryType] || <Film size={14} />}
                        </span>
                        <span className="text-xs font-semibold text-gray-300 uppercase tracking-wider">
                            {group.libraryName}
                        </span>
                    </div>

                    {/* Library Items */}
                    <div className="divide-y divide-white/5">
                        {/* B-07: the row was a <button> wrapping the play <button> —
                            invalid HTML (validateDOMNesting warning, unpredictable
                            click/focus behavior). The row is now a div with button
                            semantics; the play control stays the real button. */}
                        {group.items.map((item) => (
                            <div
                                key={item.id}
                                role="button"
                                tabIndex={0}
                                onClick={() => handleItemClick(item)}
                                onKeyDown={(e) => {
                                    if (e.key === 'Enter' || e.key === ' ') {
                                        e.preventDefault();
                                        handleItemClick(item);
                                    }
                                }}
                                className="w-full px-4 py-3 flex items-center gap-3 hover:bg-white/5 transition-colors group text-left cursor-pointer"
                            >
                                {/* Thumbnail — API image routes need the query token
                                    (an <img> can't send the Authorization header). */}
                                <div className="w-10 h-14 bg-gradient-to-br from-primary/20 to-secondary/20 rounded overflow-hidden flex-shrink-0">
                                    {item.posterPath ? (
                                        <img
                                            src={resolveCardPosterUrl(item.posterPath) ?? undefined}
                                            alt={item.title}
                                            className="w-full h-full object-cover"
                                        />
                                    ) : (
                                        <div className="w-full h-full flex items-center justify-center text-gray-500">
                                            {libraryIcons[group.libraryType] || <Film size={16} />}
                                        </div>
                                    )}
                                </div>

                                {/* Title & context — duplicate track/episode titles are
                                    indistinguishable without artist/album/series context
                                    (live data had nine identical "Caught In A Mosh" rows). */}
                                <div className="flex-1 min-w-0">
                                    <p className="text-sm font-medium text-white truncate group-hover:text-primary transition-colors">
                                        {item.title}
                                    </p>
                                    {/* Context beats explanation: a track's artist—album
                                        line already says why it matched. The reason fills
                                        in where no context exists — a movie surfaced by
                                        cast or description would otherwise show a bare
                                        year and read as noise. */}
                                    <p className="text-xs text-gray-400 truncate">
                                        {subtitleFor(item)
                                            ?? group.matchReasons?.[item.id]
                                            ?? (item.year ? String(item.year) : '')}
                                    </p>
                                </div>

                                {/* Play Button */}
                                <motion.button
                                    whileHover={{ scale: 1.1 }}
                                    whileTap={{ scale: 0.9 }}
                                    onClick={(e) => handlePlay(e, item)}
                                    className="p-2 bg-primary/20 hover:bg-primary text-primary hover:text-white rounded-full transition-colors opacity-0 group-hover:opacity-100"
                                >
                                    <Play size={14} fill="currentColor" />
                                </motion.button>
                            </div>
                        ))}
                    </div>
                </div>
                );
            })}
        </motion.div>
    );
}
