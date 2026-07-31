import { useState, useMemo } from 'react';
import { useParams, useNavigate, useSearchParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
    DndContext,
    closestCenter,
    KeyboardSensor,
    PointerSensor,
    TouchSensor,
    useSensor,
    useSensors,
    type DragEndEvent,
} from '@dnd-kit/core';
import { SortableContext, sortableKeyboardCoordinates, verticalListSortingStrategy, arrayMove } from '@dnd-kit/sortable';
import {
    Play, Shuffle, Globe, Lock, ListMusic, Trash2, Loader2, Edit3, Save,
    X as XIcon, User as UserIcon, Plus, Search, ListEnd, Copy, Sparkles, Download, ImagePlus
} from 'lucide-react';
import { motion } from 'framer-motion';
import { toast } from 'sonner';
import { playlistService, type PlaylistEntry } from '../services/playlistService';
import { useAudioStore } from '../store/audioStore';
import { useLibraries } from '../hooks/useLibrary';
import { playlistsIndexTarget, PLAYLIST_ORIGIN_PARAM } from '../lib/backNavigation';
import { SortablePlaylistItem } from '../components/playlists/SortablePlaylistItem';
import { PlaylistCover } from '../components/playlists/PlaylistCover';
import { BackButton } from '../components/ui/BackButton';
import { AddTracksPanel } from '../components/playlists/AddTracksPanel';
import { resolveBackdropUrl } from '../lib/mediaImageUrl';
import { copyPlaylistName } from '../lib/playlistNaming';
import { describeSmartRules } from '../lib/smartPlaylistPresets';
import { useMediaTokenRefresh } from '../hooks/useMediaTokenRefresh';

/**
 * Wave E1 — Playlist detail. Lists tracks, supports drag-to-reorder for owners
 * (no edit mode — desktop and touch both drag), play-from-track, remove,
 * rename, and visibility toggle.
 *
 * Research-driven choices:
 *   - No "edit mode" gate on desktop (Spotify pattern; users complain about
 *     extra clicks). Drag handle is always visible for the owner.
 *   - Optimistic reorder updates so the drag feels instant; revert + toast on
 *     server rejection.
 *   - Add-to-queue does NOT modify the playlist. "Play playlist" hydrates a
 *     fresh queue from the server data via audioStore.playPlaylist.
 *   - Confirmation gating only on destructive Delete-playlist action.
 *
 * Long-list note: research found Jellyfin playlists with 800+ tracks become
 * unusable. We rely on the existing UI being light-DOM (one row per track,
 * no nested context). For very large playlists, virtualisation is a follow-up
 * because @dnd-kit + virtualisation requires extra plumbing — deferred.
 */
export default function PlaylistDetailPage() {
    const { id = '' } = useParams<{ id: string }>();
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    // Cover art below embeds the media token; re-render when it rotates.
    useMediaTokenRefresh();
    const playPlaylist = useAudioStore(s => s.playPlaylist);
    const addToQueue = useAudioStore(s => s.addToQueue);
    const currentTrackId = useAudioStore(s => s.currentTrack?.id);

    // The playlists index is the Music library's Playlists tab, not a route of
    // its own, so the destination has to be resolved from the library list. The
    // ?from= param records which library the user opened this playlist from —
    // playlists aren't owned by a library, so it can't be inferred otherwise.
    const [searchParams] = useSearchParams();
    const { data: libraries } = useLibraries();
    const allPlaylistsHref = playlistsIndexTarget(
        libraries,
        searchParams.get(PLAYLIST_ORIGIN_PARAM),
    );

    const [isRenaming, setIsRenaming] = useState(false);
    const [draftName, setDraftName] = useState('');
    const [isEditingDescription, setIsEditingDescription] = useState(false);
    const [draftDescription, setDraftDescription] = useState('');
    const [confirmDelete, setConfirmDelete] = useState(false);
    const [showAddTracks, setShowAddTracks] = useState(false);
    const [filter, setFilter] = useState('');
    // Local reorder state — avoids re-render flicker between drop and server ack.
    const [localOrder, setLocalOrder] = useState<PlaylistEntry[] | null>(null);

    const { data: playlist, isLoading } = useQuery({
        queryKey: ['playlist', id],
        queryFn: () => playlistService.get(id),
        enabled: !!id,
    });

    // Sync local-order with server data when the query updates. Render-time
    // adjustment rather than an effect: the resync lands in the SAME render pass
    // instead of one frame later, so there is no flicker frame where a reorder
    // ack briefly shows stale order. (react.dev: "adjusting state when props
    // change".)
    const [syncedItems, setSyncedItems] = useState<PlaylistEntry[] | null>(null);
    if (playlist && playlist.items !== syncedItems) {
        setSyncedItems(playlist.items);
        setLocalOrder(playlist.items);
    }

    const sensors = useSensors(
        useSensor(PointerSensor, { activationConstraint: { distance: 6 } }),
        // Touch: long-press 200ms before drag starts. Spotify-style: prevents
        // accidental drag-on-tap, especially on mobile cards where the user is
        // scrolling.
        useSensor(TouchSensor, { activationConstraint: { delay: 200, tolerance: 8 } }),
        useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
    );

    const updateMutation = useMutation({
        mutationFn: (patch: { name?: string; description?: string | null; isPublic?: boolean }) =>
            playlistService.update(id, patch),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['playlist', id] }),
        onError: (e: unknown) => toast.error(e instanceof Error ? e.message : 'Could not update playlist'),
    });

    const reorderMutation = useMutation({
        mutationFn: (itemIds: string[]) => playlistService.reorder(id, itemIds),
        onError: (e: unknown) => {
            // Revert local optimistic state by refetching from server.
            queryClient.invalidateQueries({ queryKey: ['playlist', id] });
            toast.error(e instanceof Error ? e.message : 'Reorder failed; reverted');
        },
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['playlists'] }),
    });

    const removeMutation = useMutation({
        mutationFn: (playlistItemId: string) => playlistService.removeItem(id, playlistItemId),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['playlist', id] });
            queryClient.invalidateQueries({ queryKey: ['playlists'] });
        },
        onError: (e: unknown) => toast.error(e instanceof Error ? e.message : 'Could not remove track'),
    });

    /**
     * "Save a copy" for a playlist someone else shared. Without it a shared
     * playlist is a dead end — readable, playable, but impossible to build on,
     * since every mutation endpoint is owner-only.
     *
     * The copy is always private regardless of the original's visibility:
     * sharing is an explicit act (Playlist.IsPublic defaults to false by
     * design), and inheriting it would re-share someone else's list on the new
     * owner's behalf.
     */
    const copyMutation = useMutation({
        mutationFn: async (source: { name: string; description: string | null; mediaItemIds: string[] }) => {
            const created = await playlistService.create({
                name: copyPlaylistName(source.name),
                description: source.description ?? undefined,
                isPublic: false,
            });
            if (source.mediaItemIds.length > 0) {
                await playlistService.addItems(created.id, source.mediaItemIds);
            }
            return created;
        },
        onSuccess: (created) => {
            queryClient.invalidateQueries({ queryKey: ['playlists'] });
            toast.success(`Saved as "${created.name}"`);
            // Keep the origin so the copy's own "All playlists" returns to the
            // library the user came from, exactly as the original's does.
            const origin = searchParams.get(PLAYLIST_ORIGIN_PARAM);
            navigate(origin
                ? `/playlists/${created.id}?${PLAYLIST_ORIGIN_PARAM}=${encodeURIComponent(origin)}`
                : `/playlists/${created.id}`);
        },
        onError: (e: unknown) => toast.error(e instanceof Error ? e.message : 'Could not save a copy'),
    });

    /**
     * Downloads the playlist as M3U. Built client-side from the response text
     * rather than pointing the browser at the endpoint, because that request
     * needs the Authorization header a plain navigation cannot send.
     */
    const exportMutation = useMutation({
        mutationFn: () => playlistService.exportM3u(id),
        onSuccess: (content) => {
            const url = URL.createObjectURL(new Blob([content], { type: 'audio/x-mpegurl' }));
            const link = document.createElement('a');
            link.href = url;
            link.download = `${(playlist?.name ?? 'playlist').replace(/[\\/:*?"<>|]/g, '_')}.m3u`;
            document.body.appendChild(link);
            link.click();
            link.remove();
            URL.revokeObjectURL(url);
        },
        onError: (e: unknown) => toast.error(e instanceof Error ? e.message : 'Could not export playlist'),
    });

    const coverMutation = useMutation({
        mutationFn: (file: File) => playlistService.uploadCover(id, file),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['playlist', id] });
            queryClient.invalidateQueries({ queryKey: ['playlists'] });
            toast.success('Cover updated');
        },
        onError: (e: unknown) => toast.error(e instanceof Error ? e.message : 'Could not set the cover'),
    });

    const removeCoverMutation = useMutation({
        mutationFn: () => playlistService.deleteCover(id),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['playlist', id] });
            queryClient.invalidateQueries({ queryKey: ['playlists'] });
            toast.success('Cover removed');
        },
        onError: (e: unknown) => toast.error(e instanceof Error ? e.message : 'Could not remove the cover'),
    });

    const deleteMutation = useMutation({
        mutationFn: () => playlistService.delete(id),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['playlists'] });
            toast.success('Playlist deleted');
            // Same destination as the "All playlists" link — '/playlists' is not
            // a route and would have redirected to the home page.
            navigate(allPlaylistsHref, { replace: true });
        },
        onError: (e: unknown) => toast.error(e instanceof Error ? e.message : 'Could not delete playlist'),
    });

    const items = useMemo(() => localOrder ?? [], [localOrder]);

    // A playlist owns no artwork, so it borrows its tracks'. Distinct paths only —
    // a single-album playlist should show one sleeve, not the same one four times.
    // An uploaded cover wins outright — the mosaic exists only because a playlist
    // has no artwork of its own, and once it does, borrowing its tracks' covers
    // would be overriding an explicit choice.
    const customCover = playlist?.coverImagePath ?? null;
    const coverPaths = useMemo(() => {
        if (customCover) return [customCover];
        const seen: string[] = [];
        for (const entry of items) {
            const path = entry.media.posterPath;
            if (path && !seen.includes(path)) seen.push(path);
            if (seen.length === 4) break;
        }
        return seen;
    }, [items, customCover]);

    // Precomputed so a filtered row can report its true playlist position without
    // an indexOf scan per row — that is quadratic, and long playlists are exactly
    // the case the filter exists to serve.
    const positionById = useMemo(() => {
        const map = new Map<string, number>();
        items.forEach((entry, idx) => map.set(entry.playlistItemId, idx + 1));
        return map;
    }, [items]);

    // Filtering is a display concern only — `items` stays the source of truth for
    // playback and reordering, so a filtered view still plays the whole playlist.
    const normalizedFilter = filter.trim().toLowerCase();
    const visibleItems = useMemo(() => {
        if (!normalizedFilter) return items;
        return items.filter(entry => {
            const { title, metadata } = entry.media;
            const artist = (metadata?.artist as string) ?? '';
            const album = (metadata?.album as string) ?? '';
            return `${title} ${artist} ${album}`.toLowerCase().includes(normalizedFilter);
        });
    }, [items, normalizedFilter]);

    const handleDragEnd = (event: DragEndEvent) => {
        const { active, over } = event;
        if (!over || active.id === over.id || !localOrder) return;

        const oldIndex = localOrder.findIndex(e => e.playlistItemId === active.id);
        const newIndex = localOrder.findIndex(e => e.playlistItemId === over.id);
        if (oldIndex < 0 || newIndex < 0) return;

        const reordered = arrayMove(localOrder, oldIndex, newIndex);
        setLocalOrder(reordered); // optimistic
        reorderMutation.mutate(reordered.map(e => e.playlistItemId));
    };

    const handlePlayFrom = (entry: PlaylistEntry) => {
        if (!localOrder) return;
        const tracks = localOrder.map(e => e.media);
        playPlaylist(tracks, entry.media);
    };

    const handlePlayAll = () => {
        if (items.length === 0) return;
        playPlaylist(items.map(e => e.media));
    };

    /**
     * Shuffle plays a randomised copy rather than toggling the store's shuffle
     * mode: the mode is a persisted user preference, and a one-off "play this
     * playlist shuffled" shouldn't silently rewrite it for everything after.
     */
    const handleShuffle = () => {
        if (items.length === 0) return;
        const tracks = items.map(e => e.media);
        for (let i = tracks.length - 1; i > 0; i--) {
            const j = Math.floor(Math.random() * (i + 1));
            [tracks[i], tracks[j]] = [tracks[j], tracks[i]];
        }
        playPlaylist(tracks);
    };

    const handleQueueAll = () => {
        if (items.length === 0) return;
        items.forEach(e => addToQueue(e.media));
        toast.success(`${items.length} ${items.length === 1 ? 'track' : 'tracks'} added to queue`);
    };

    const startRename = () => {
        if (!playlist) return;
        setDraftName(playlist.name);
        setIsRenaming(true);
    };

    const submitRename = () => {
        const trimmed = draftName.trim();
        if (!trimmed || !playlist) return;
        if (trimmed === playlist.name) { setIsRenaming(false); return; }
        updateMutation.mutate(
            { name: trimmed },
            { onSuccess: () => setIsRenaming(false) }
        );
    };

    const startEditDescription = () => {
        if (!playlist) return;
        setDraftDescription(playlist.description ?? '');
        setIsEditingDescription(true);
    };

    const submitDescription = () => {
        const trimmed = draftDescription.trim();
        if (!playlist) return;
        if (trimmed === (playlist.description ?? '')) { setIsEditingDescription(false); return; }
        // Empty string clears it: the server maps whitespace-only to null.
        updateMutation.mutate(
            { description: trimmed },
            { onSuccess: () => setIsEditingDescription(false) }
        );
    };

    if (isLoading) {
        return (
            <div className="min-h-screen flex items-center justify-center text-gray-400">
                <Loader2 className="w-6 h-6 animate-spin" />
            </div>
        );
    }
    if (!playlist) {
        return (
            <div className="min-h-screen flex items-center justify-center text-gray-400">
                Playlist not found.
            </div>
        );
    }

    // SR-WI-063: durationSeconds is the only duration field. (The old `Number(e.media.duration)`
    // silently produced 0 for formatted strings like "3m 45s" — this also fixes that.)
    const totalSeconds = items.reduce((acc, e) => acc + (e.media.durationSeconds || 0), 0);
    const durationLabel = formatDuration(totalSeconds);

    const backdrop = resolveBackdropUrl(coverPaths[0]);

    // A smart playlist's membership is a query re-run on every read, so the
    // affordances that edit membership by hand — drag, remove, add tracks — are
    // not merely disabled but absent: the server rejects them, and offering a
    // control that cannot work is worse than not offering it.
    const isSmart = playlist.kind === 'Smart';
    const canEditTracks = playlist.isOwner && !isSmart;

    return (
        <div className="min-h-screen bg-background relative overflow-x-hidden text-white">
            {/* Blurred cover behind the page — the same depth treatment every other
                detail page gets (MediaDetailLayout). Playlists were the one surface
                still sitting on flat background. */}
            <div className="fixed inset-0 w-full h-full overflow-hidden pointer-events-none">
                {backdrop ? (
                    <>
                        <img
                            src={backdrop}
                            alt=""
                            referrerPolicy="no-referrer"
                            className="w-full h-full object-cover object-top opacity-30 blur-xl scale-110"
                        />
                        <div className="absolute inset-0 bg-background/60" />
                        <div className="absolute inset-0 bg-gradient-to-t from-background via-background/40 to-transparent" />
                    </>
                ) : (
                    <div className="w-full h-full bg-gradient-to-b from-primary/10 to-background" />
                )}
            </div>

            <div className="relative z-10 w-full px-4 lg:px-6 pt-4 lg:pt-6 pb-12">
                {/* Same back control as every other detail surface. The destination
                    is still the Music library's Playlists tab — there is no
                    /playlists route (see playlistsIndexTarget). */}
                <BackButton to={allPlaylistsHref} />

                {/* Same two-column shell every media detail page uses
                    (MediaDetailLayout): artwork in a fixed left rail, everything
                    else filling the rest of the width. This page used to be capped
                    at max-w-5xl and centred, which stranded the track list in the
                    middle of the screen with empty gutters either side. */}
                <div className="flex flex-col lg:flex-row gap-8 lg:gap-12">
                    <div className="flex-shrink-0 w-full sm:w-64 md:w-72 lg:w-80 mx-auto lg:mx-0">
                        <motion.div
                            initial={{ opacity: 0, y: 20 }}
                            animate={{ opacity: 1, y: 0 }}
                        >
                            {/* Square, like the album/artist art the rail carries
                                elsewhere — a playlist of music should not be
                                cropped to a 2:3 poster. */}
                            <PlaylistCover
                                coverPaths={coverPaths}
                                size="hero"
                                className="w-full aspect-square rounded-xl shadow-2xl ring-1 ring-white/10"
                                iconClassName="w-20 h-20"
                            />
                        </motion.div>

                        {playlist.isOwner && (
                            <div className="mt-3 flex items-center justify-center gap-2 text-xs">
                                <label className="inline-flex items-center gap-1.5 px-3 py-2 rounded-lg bg-white/5 hover:bg-white/10 text-gray-300 cursor-pointer transition-colors min-h-[36px] focus-within:ring-2 focus-within:ring-blue-400">
                                    {coverMutation.isPending
                                        ? <Loader2 className="w-3.5 h-3.5 animate-spin" />
                                        : <ImagePlus className="w-3.5 h-3.5" />}
                                    {playlist.coverImagePath ? 'Change cover' : 'Set cover'}
                                    <input
                                        type="file"
                                        accept="image/*"
                                        className="sr-only"
                                        disabled={coverMutation.isPending}
                                        onChange={(e) => {
                                            const file = e.target.files?.[0];
                                            // Cleared first so re-picking the same file still fires.
                                            e.target.value = '';
                                            if (file) coverMutation.mutate(file);
                                        }}
                                    />
                                </label>

                                {/* Only offered when there is an upload to remove —
                                    the generated mosaic is not something you can delete. */}
                                {playlist.coverImagePath && (
                                    <button
                                        type="button"
                                        onClick={() => removeCoverMutation.mutate()}
                                        disabled={removeCoverMutation.isPending}
                                        className="inline-flex items-center gap-1.5 px-3 py-2 rounded-lg text-gray-400 hover:text-white hover:bg-white/5 transition-colors min-h-[36px] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                                    >
                                        <XIcon className="w-3.5 h-3.5" />
                                        Remove
                                    </button>
                                )}
                            </div>
                        )}
                    </div>

                    <div className="flex-1 min-w-0">
                        <motion.div
                            initial={{ opacity: 0, y: 8 }}
                            animate={{ opacity: 1, y: 0 }}
                            className="mb-8"
                        >
                            <div className="text-xs uppercase tracking-wider text-gray-500 font-bold mb-2 flex items-center gap-1.5">
                                {isSmart && <Sparkles className="w-3 h-3 text-primary" />}
                                {isSmart ? 'Automatic Playlist' : 'Playlist'}
                            </div>
                            {isRenaming ? (
                                <form
                                    onSubmit={(e) => { e.preventDefault(); submitRename(); }}
                                    className="flex items-center gap-2 mb-3"
                                >
                                    <input
                                        autoFocus
                                        value={draftName}
                                        onChange={(e) => setDraftName(e.target.value)}
                                        maxLength={120}
                                        className="bg-black/30 border border-white/10 rounded-lg px-3 py-2 text-2xl sm:text-4xl font-bold text-white focus:outline-none focus:border-primary flex-1"
                                    />
                                    <button type="submit" className="p-2 text-primary hover:bg-white/5 rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400" aria-label="Save name">
                                        <Save className="w-5 h-5" />
                                    </button>
                                    <button type="button" onClick={() => setIsRenaming(false)} className="p-2 text-gray-400 hover:bg-white/5 rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400" aria-label="Cancel rename">
                                        <XIcon className="w-5 h-5" />
                                    </button>
                                </form>
                            ) : (
                                <div className="flex items-center gap-3 mb-3">
                                    <h1 className="text-3xl sm:text-5xl font-bold truncate">{playlist.name}</h1>
                                    {playlist.isOwner && (
                                        <button
                                            type="button"
                                            onClick={startRename}
                                            aria-label="Rename playlist"
                                            className="text-gray-500 hover:text-white focus-visible:text-white focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded p-2"
                                        >
                                            <Edit3 className="w-4 h-4" />
                                        </button>
                                    )}
                                </div>
                            )}
                            <div className="text-sm text-gray-400 flex items-center gap-2 flex-wrap">
                                {playlist.isPublic
                                    ? <span className="inline-flex items-center gap-1.5"><Globe className="w-3.5 h-3.5" /> Public</span>
                                    : <span className="inline-flex items-center gap-1.5"><Lock className="w-3.5 h-3.5" /> Private</span>}
                                <span>·</span>
                                {!playlist.isOwner && (
                                    <>
                                        <span className="inline-flex items-center gap-1.5"><UserIcon className="w-3.5 h-3.5" /> By {playlist.ownerUsername}</span>
                                        <span>·</span>
                                    </>
                                )}
                                <span>{items.length} {items.length === 1 ? 'track' : 'tracks'}</span>
                                {totalSeconds > 0 && <><span>·</span><span>{durationLabel}</span></>}
                            </div>

                            {/* What this playlist is actually selecting. Someone
                                returning months later needs to know why these tracks
                                and not others; the rules answer that where the list
                                alone cannot. Owner-only — the server withholds rules
                                from everyone else. */}
                            {isSmart && playlist.rules && (
                                <div className="mt-3 inline-flex items-start gap-2 text-xs text-gray-400 bg-white/5 border border-white/10 rounded-lg px-3 py-2">
                                    <Sparkles className="w-3.5 h-3.5 text-primary shrink-0 mt-px" />
                                    <span>
                                        {describeSmartRules(playlist.rules)}
                                        <span className="block text-gray-500 mt-0.5">
                                            Updates itself as your library and listening change.
                                        </span>
                                    </span>
                                </div>
                            )}

                            {/* Description — stored since Wave E1 but never shown or
                                editable anywhere in the client until now. */}
                            {isEditingDescription ? (
                                <form
                                    onSubmit={(e) => { e.preventDefault(); submitDescription(); }}
                                    className="mt-3 flex items-start gap-2"
                                >
                                    <textarea
                                        autoFocus
                                        value={draftDescription}
                                        onChange={(e) => setDraftDescription(e.target.value)}
                                        maxLength={500}
                                        rows={2}
                                        placeholder="What's this playlist for?"
                                        className="flex-1 bg-black/30 border border-white/10 rounded-lg px-3 py-2 text-sm text-white placeholder-gray-600 focus:outline-none focus:border-primary resize-none max-w-3xl"
                                    />
                                    <button type="submit" className="p-2 text-primary hover:bg-white/5 rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400" aria-label="Save description">
                                        <Save className="w-4 h-4" />
                                    </button>
                                    <button type="button" onClick={() => setIsEditingDescription(false)} className="p-2 text-gray-400 hover:bg-white/5 rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400" aria-label="Cancel description edit">
                                        <XIcon className="w-4 h-4" />
                                    </button>
                                </form>
                            ) : playlist.description ? (
                                // Prose stays measure-capped even though the page is
                                // full-bleed: a 200-character description set across a
                                // 2000px monitor is unreadable.
                                <p className="mt-3 text-sm text-gray-300 max-w-3xl leading-relaxed flex items-start gap-2">
                                    <span className="flex-1">{playlist.description}</span>
                                    {playlist.isOwner && (
                                        <button
                                            type="button"
                                            onClick={startEditDescription}
                                            aria-label="Edit description"
                                            className="text-gray-500 hover:text-white focus-visible:text-white focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded p-1 shrink-0"
                                        >
                                            <Edit3 className="w-3.5 h-3.5" />
                                        </button>
                                    )}
                                </p>
                            ) : playlist.isOwner ? (
                                <button
                                    type="button"
                                    onClick={startEditDescription}
                                    className="mt-3 text-sm text-gray-500 hover:text-gray-300 focus-visible:text-gray-300 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded inline-flex items-center gap-1.5 px-1 py-0.5"
                                >
                                    <Plus className="w-3.5 h-3.5" />
                                    Add a description
                                </button>
                            ) : null}
                        </motion.div>

                        {/* Toolbar — Play/Shuffle mirror the album detail view's pair
                            so the two music surfaces read as the same app. */}
                        <div className="flex flex-wrap items-center gap-3 mb-6">
                            <button
                                type="button"
                                onClick={handlePlayAll}
                                disabled={items.length === 0}
                                className="bg-gradient-to-r from-blue-600 to-violet-600 disabled:from-gray-600 disabled:to-gray-700 disabled:opacity-50 disabled:cursor-not-allowed text-white px-6 py-3 rounded-full font-bold flex items-center gap-2 transition-all hover:scale-[1.02] active:scale-95 shadow-lg shadow-violet-500/30 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 min-h-[44px]"
                            >
                                <Play className="w-5 h-5 fill-current" />
                                Play
                            </button>

                            <button
                                type="button"
                                onClick={handleShuffle}
                                disabled={items.length === 0}
                                className="bg-white/10 hover:bg-white/20 disabled:opacity-40 disabled:cursor-not-allowed text-white px-6 py-3 rounded-full font-medium flex items-center gap-2 transition-colors border border-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 min-h-[44px]"
                            >
                                <Shuffle className="w-5 h-5" />
                                Shuffle
                            </button>

                            <button
                                type="button"
                                onClick={handleQueueAll}
                                disabled={items.length === 0}
                                title="Add every track to the playback queue"
                                className="inline-flex items-center gap-2 px-4 py-3 rounded-lg bg-white/5 hover:bg-white/10 disabled:opacity-40 disabled:cursor-not-allowed focus-visible:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 transition-colors min-h-[44px] text-sm"
                            >
                                <ListEnd className="w-4 h-4" />
                                Queue
                            </button>

                            <button
                                type="button"
                                onClick={() => exportMutation.mutate()}
                                disabled={items.length === 0 || exportMutation.isPending}
                                title="Download as an M3U file for other players"
                                className="inline-flex items-center gap-2 px-4 py-3 rounded-lg bg-white/5 hover:bg-white/10 disabled:opacity-40 disabled:cursor-not-allowed focus-visible:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 transition-colors min-h-[44px] text-sm"
                            >
                                {exportMutation.isPending
                                    ? <Loader2 className="w-4 h-4 animate-spin" />
                                    : <Download className="w-4 h-4" />}
                                Export
                            </button>

                            {!playlist.isOwner && (
                                <button
                                    type="button"
                                    onClick={() => copyMutation.mutate({
                                        name: playlist.name,
                                        description: playlist.description,
                                        mediaItemIds: items.map(e => e.media.id),
                                    })}
                                    disabled={copyMutation.isPending}
                                    title="Save this playlist to your own, as a private copy"
                                    className="inline-flex items-center gap-2 px-4 py-3 rounded-lg bg-white/5 hover:bg-white/10 disabled:opacity-50 focus-visible:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 transition-colors min-h-[44px] text-sm"
                                >
                                    {copyMutation.isPending
                                        ? <Loader2 className="w-4 h-4 animate-spin" />
                                        : <Copy className="w-4 h-4" />}
                                    Save a copy
                                </button>
                            )}

                            {playlist.isOwner && (
                                <>
                                    {canEditTracks && (
                                        <button
                                            type="button"
                                            onClick={() => setShowAddTracks(v => !v)}
                                            aria-expanded={showAddTracks}
                                            className="inline-flex items-center gap-2 px-4 py-3 rounded-lg bg-white/5 hover:bg-white/10 focus-visible:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 transition-colors min-h-[44px] text-sm"
                                        >
                                            <Plus className="w-4 h-4" />
                                            Add tracks
                                        </button>
                                    )}

                                    {/* Sharing is unavailable for automatic playlists —
                                        their contents come from the owner's own
                                        favourites and listening. */}
                                    {!isSmart && (
                                        <button
                                            type="button"
                                            role="switch"
                                            aria-checked={playlist.isPublic}
                                            onClick={() => updateMutation.mutate({ isPublic: !playlist.isPublic })}
                                            className="inline-flex items-center gap-2 px-4 py-3 rounded-lg bg-white/5 hover:bg-white/10 focus-visible:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 transition-colors min-h-[44px] text-sm"
                                        >
                                            {playlist.isPublic ? <Globe className="w-4 h-4" /> : <Lock className="w-4 h-4" />}
                                            {playlist.isPublic ? 'Public' : 'Private'}
                                        </button>
                                    )}

                                    <button
                                        type="button"
                                        onClick={() => setConfirmDelete(true)}
                                        className="inline-flex items-center gap-2 px-4 py-3 rounded-lg text-red-400 hover:bg-red-500/10 focus-visible:bg-red-500/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-400 transition-colors min-h-[44px] text-sm"
                                    >
                                        <Trash2 className="w-4 h-4" />
                                        Delete
                                    </button>
                                </>
                            )}
                        </div>

                        {showAddTracks && canEditTracks && (
                            <AddTracksPanel
                                playlistId={id}
                                existingMediaItemIds={items.map(e => e.media.id)}
                                onClose={() => setShowAddTracks(false)}
                            />
                        )}

                        {/* Filter — long playlists are the known pain point (Jellyfin
                            lists of 800+ tracks). Appears once a list needs it. */}
                        {items.length >= 10 && (
                            <div className="flex items-center gap-2 mb-3 px-3 py-2 bg-white/5 border border-white/10 rounded-lg max-w-md">
                                <Search className="w-4 h-4 text-gray-500 shrink-0" />
                                <input
                                    type="search"
                                    value={filter}
                                    onChange={(e) => setFilter(e.target.value)}
                                    placeholder="Filter this playlist…"
                                    aria-label="Filter tracks in this playlist"
                                    className="flex-1 bg-transparent text-sm text-white placeholder-gray-500 focus:outline-none min-h-[32px]"
                                />
                                {normalizedFilter && (
                                    <span className="text-xs text-gray-500 shrink-0">
                                        {visibleItems.length} of {items.length}
                                    </span>
                                )}
                            </div>
                        )}

                        {/* Track list */}
                        {items.length === 0 ? (
                            <div className="bg-white/5 border border-white/10 rounded-xl p-12 text-center">
                                <ListMusic className="w-12 h-12 text-gray-600 mx-auto mb-3" />
                                {isSmart ? (
                                    // Nothing to add by hand here — an empty automatic
                                    // playlist means its rules currently match nothing,
                                    // which is a statement about the library.
                                    <p className="text-gray-400 text-sm">
                                        Nothing matches this playlist's rules yet. It will fill in as your
                                        library grows and you listen.
                                    </p>
                                ) : playlist.isOwner ? (
                                    <>
                                        <p className="text-gray-400 text-sm mb-4">
                                            No tracks yet. Search your library and add the first one.
                                        </p>
                                        <button
                                            type="button"
                                            onClick={() => setShowAddTracks(true)}
                                            className="inline-flex items-center gap-2 px-5 py-2.5 bg-primary hover:bg-primary/90 text-white rounded-lg font-medium transition-all shadow-lg shadow-primary/20 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 min-h-[44px] text-sm"
                                        >
                                            <Plus className="w-4 h-4" />
                                            Add tracks
                                        </button>
                                    </>
                                ) : (
                                    <p className="text-gray-400 text-sm">This playlist is empty.</p>
                                )}
                            </div>
                        ) : visibleItems.length === 0 ? (
                            <div className="bg-white/5 border border-white/10 rounded-xl p-10 text-center text-sm text-gray-400">
                                No tracks in this playlist match “{filter.trim()}”.
                            </div>
                        ) : (
                            <DndContext
                                sensors={sensors}
                                collisionDetection={closestCenter}
                                onDragEnd={handleDragEnd}
                            >
                                <SortableContext
                                    items={visibleItems.map(e => e.playlistItemId)}
                                    strategy={verticalListSortingStrategy}
                                >
                                    <div className="bg-white/5 border border-white/10 rounded-xl divide-y divide-white/5 backdrop-blur-sm">
                                        {visibleItems.map((entry) => (
                                            <SortablePlaylistItem
                                                key={entry.playlistItemId}
                                                entry={entry}
                                                // Position is the track's place in the PLAYLIST, not in
                                                // the filtered view — a filtered row still reports where
                                                // it actually sits, and the numbers stay stable.
                                                position={positionById.get(entry.playlistItemId) ?? 0}
                                                canEdit={canEditTracks}
                                                isCurrent={entry.media.id === currentTrackId}
                                                dragDisabled={!!normalizedFilter}
                                                onPlay={() => handlePlayFrom(entry)}
                                                onAddToQueue={() => {
                                                    addToQueue(entry.media);
                                                    toast.success(`"${entry.media.title}" added to queue`);
                                                }}
                                                onRemove={
                                                    canEditTracks
                                                        ? () => removeMutation.mutate(entry.playlistItemId)
                                                        : undefined
                                                }
                                            />
                                        ))}
                                    </div>
                                </SortableContext>
                            </DndContext>
                        )}
                    </div>
                </div>
            </div>

            {/* Delete confirmation */}
            {confirmDelete && (
                <div className="fixed inset-0 bg-black/60 backdrop-blur-sm flex items-center justify-center z-50 p-4">
                    <motion.div
                        initial={{ opacity: 0, scale: 0.95 }}
                        animate={{ opacity: 1, scale: 1 }}
                        className="bg-[#1a1a1a] rounded-xl p-6 w-full max-w-md border border-red-500/20 shadow-2xl"
                    >
                        <h2 className="text-xl font-bold text-red-400 mb-2 flex items-center gap-2">
                            <Trash2 className="w-5 h-5" />
                            Delete playlist
                        </h2>
                        <p className="text-sm text-gray-300 mb-6">
                            "{playlist.name}" will be permanently deleted. The tracks themselves stay in your library.
                        </p>
                        <div className="flex gap-2">
                            <button
                                type="button"
                                onClick={() => setConfirmDelete(false)}
                                className="flex-1 px-4 py-2.5 rounded-lg text-gray-300 hover:bg-white/5 focus-visible:bg-white/5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 min-h-[44px]"
                            >
                                Cancel
                            </button>
                            <button
                                type="button"
                                onClick={() => deleteMutation.mutate()}
                                disabled={deleteMutation.isPending}
                                className="flex-1 px-4 py-2.5 rounded-lg bg-red-500 hover:bg-red-600 text-white font-medium disabled:opacity-50 flex items-center justify-center gap-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-400 min-h-[44px]"
                            >
                                {deleteMutation.isPending && <Loader2 className="w-4 h-4 animate-spin" />}
                                Delete
                            </button>
                        </div>
                    </motion.div>
                </div>
            )}
        </div>
    );
}

function formatDuration(seconds: number): string {
    if (seconds <= 0) return '0:00';
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    if (h > 0) return `${h}h ${m}m`;
    return `${m}m`;
}
