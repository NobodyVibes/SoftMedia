import { useState, useMemo, useEffect } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
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
    ArrowLeft, Play, Globe, Lock, ListMusic, Trash2, Loader2, Edit3, Save, X as XIcon, User as UserIcon
} from 'lucide-react';
import { motion } from 'framer-motion';
import { toast } from 'sonner';
import { playlistService, type PlaylistEntry } from '../services/playlistService';
import { useAudioStore } from '../store/audioStore';
import { SortablePlaylistItem } from '../components/playlists/SortablePlaylistItem';

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
    const playPlaylist = useAudioStore(s => s.playPlaylist);

    const [isRenaming, setIsRenaming] = useState(false);
    const [draftName, setDraftName] = useState('');
    const [confirmDelete, setConfirmDelete] = useState(false);
    // Local reorder state — avoids re-render flicker between drop and server ack.
    const [localOrder, setLocalOrder] = useState<PlaylistEntry[] | null>(null);

    const { data: playlist, isLoading } = useQuery({
        queryKey: ['playlist', id],
        queryFn: () => playlistService.get(id),
        enabled: !!id,
    });

    // Sync local-order with server data when the query updates.
    useEffect(() => {
        if (playlist) {
            setLocalOrder(playlist.items);
        }
    }, [playlist]);

    const sensors = useSensors(
        useSensor(PointerSensor, { activationConstraint: { distance: 6 } }),
        // Touch: long-press 200ms before drag starts. Spotify-style: prevents
        // accidental drag-on-tap, especially on mobile cards where the user is
        // scrolling.
        useSensor(TouchSensor, { activationConstraint: { delay: 200, tolerance: 8 } }),
        useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
    );

    const updateMutation = useMutation({
        mutationFn: (patch: { name?: string; isPublic?: boolean }) => playlistService.update(id, patch),
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

    const deleteMutation = useMutation({
        mutationFn: () => playlistService.delete(id),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['playlists'] });
            toast.success('Playlist deleted');
            navigate('/playlists');
        },
        onError: (e: unknown) => toast.error(e instanceof Error ? e.message : 'Could not delete playlist'),
    });

    const items = useMemo(() => localOrder ?? [], [localOrder]);

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
        if (!localOrder || localOrder.length === 0) return;
        playPlaylist(localOrder.map(e => e.media));
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

    const totalSeconds = items.reduce((acc, e) => acc + (Number(e.media.duration) || 0), 0);
    const durationLabel = formatDuration(totalSeconds);

    return (
        <div className="min-h-screen bg-background p-6 text-white">
            <div className="max-w-5xl mx-auto">
                {/* Back link */}
                <Link
                    to="/playlists"
                    className="inline-flex items-center gap-2 text-sm text-gray-400 hover:text-white focus-visible:text-white focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded mb-6 px-2 py-1"
                >
                    <ArrowLeft className="w-4 h-4" />
                    All playlists
                </Link>

                {/* Header */}
                <motion.div
                    initial={{ opacity: 0, y: 8 }}
                    animate={{ opacity: 1, y: 0 }}
                    className="flex flex-col sm:flex-row sm:items-end gap-6 mb-8"
                >
                    <div className="w-32 h-32 sm:w-40 sm:h-40 bg-brand-gradient rounded-xl flex items-center justify-center shrink-0 shadow-2xl">
                        <ListMusic className="w-16 h-16 text-white" />
                    </div>
                    <div className="flex-1 min-w-0">
                        <div className="text-xs uppercase tracking-wider text-gray-500 font-bold mb-2">Playlist</div>
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
                                <h1 className="text-2xl sm:text-4xl font-bold truncate">{playlist.name}</h1>
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
                    </div>
                </motion.div>

                {/* Toolbar */}
                <div className="flex flex-wrap items-center gap-3 mb-6">
                    <button
                        type="button"
                        onClick={handlePlayAll}
                        disabled={items.length === 0}
                        className="inline-flex items-center gap-2 px-6 py-3 rounded-full bg-primary hover:bg-primary/90 text-white font-medium shadow-lg shadow-primary/20 transition-all disabled:opacity-40 disabled:cursor-not-allowed focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 min-h-[44px]"
                    >
                        <Play className="w-4 h-4 fill-white" />
                        Play
                    </button>

                    {playlist.isOwner && (
                        <>
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

                {/* Track list */}
                {items.length === 0 ? (
                    <div className="bg-white/5 border border-white/10 rounded-xl p-12 text-center">
                        <ListMusic className="w-12 h-12 text-gray-600 mx-auto mb-3" />
                        <p className="text-gray-400 text-sm">
                            {playlist.isOwner
                                ? 'No tracks yet. Add some from your music library.'
                                : 'This playlist is empty.'}
                        </p>
                    </div>
                ) : (
                    <DndContext
                        sensors={sensors}
                        collisionDetection={closestCenter}
                        onDragEnd={handleDragEnd}
                    >
                        <SortableContext
                            items={items.map(e => e.playlistItemId)}
                            strategy={verticalListSortingStrategy}
                        >
                            <div className="bg-white/5 border border-white/10 rounded-xl divide-y divide-white/5">
                                {items.map((entry, idx) => (
                                    <SortablePlaylistItem
                                        key={entry.playlistItemId}
                                        entry={entry}
                                        position={idx + 1}
                                        canEdit={playlist.isOwner}
                                        onPlay={() => handlePlayFrom(entry)}
                                        onRemove={
                                            playlist.isOwner
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
