import { useState, useRef, useEffect } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Plus, Lock, Globe, Loader2, ListMusic } from 'lucide-react';
import { toast } from 'sonner';
import { playlistService, type PlaylistSummary } from '../../services/playlistService';

interface AddToPlaylistMenuProps {
    /** Audio MediaItem ids to add. */
    mediaItemIds: string[];
    /** Closes the popover (parent owns open/close state). */
    onClose: () => void;
    /**
     * Whether the menu should open below or above the trigger. "down" (default)
     * works for most placements; "up" is for triggers near the bottom of the
     * viewport (e.g. the persistent audio player bar) where opening downward
     * would clip off-screen.
     */
    placement?: 'down' | 'up';
    /**
     * Horizontal alignment of the menu relative to its anchor parent. Defaults
     * to "right" so the menu's right edge aligns with the parent's right edge
     * (suits track-row "more" buttons). Use "left" to align the left edges
     * when the trigger sits on the left side of a wide bar.
     */
    align?: 'left' | 'right';
}

/**
 * Wave E1 — popover for "Add to playlist". Critical research finding from
 * Jellyfin's feature requests: existing implementations cause the page to
 * jump to the top after adding, frustrating users. This popover:
 *   - never navigates,
 *   - posts to /playlists/{id}/items,
 *   - shows a non-blocking toast on success,
 *   - lets the user create a new playlist inline without leaving the page.
 *
 * Parent renders this inside a `<div class="relative">` so the absolute
 * positioning anchors against the trigger.
 */
export function AddToPlaylistMenu({ mediaItemIds, onClose, placement = 'down', align = 'right' }: AddToPlaylistMenuProps) {
    const queryClient = useQueryClient();
    const [showCreate, setShowCreate] = useState(false);
    const [newName, setNewName] = useState('');
    const containerRef = useRef<HTMLDivElement>(null);

    const { data: playlists = [], isLoading } = useQuery<PlaylistSummary[]>({
        queryKey: ['playlists'],
        queryFn: playlistService.list,
    });

    const ownPlaylists = playlists.filter(p => p.isOwner);

    // Click-outside + Escape to close.
    useEffect(() => {
        const onPointer = (e: PointerEvent) => {
            if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
                onClose();
            }
        };
        const onKey = (e: KeyboardEvent) => {
            if (e.key === 'Escape') onClose();
        };
        document.addEventListener('pointerdown', onPointer);
        document.addEventListener('keydown', onKey);
        return () => {
            document.removeEventListener('pointerdown', onPointer);
            document.removeEventListener('keydown', onKey);
        };
    }, [onClose]);

    const addMutation = useMutation({
        mutationFn: ({ playlistId, ids }: { playlistId: string; ids: string[] }) =>
            playlistService.addItems(playlistId, ids),
        onSuccess: (_, vars) => {
            queryClient.invalidateQueries({ queryKey: ['playlists'] });
            queryClient.invalidateQueries({ queryKey: ['playlist', vars.playlistId] });
            const playlist = ownPlaylists.find(p => p.id === vars.playlistId);
            const trackWord = mediaItemIds.length === 1 ? 'Track' : `${mediaItemIds.length} tracks`;
            toast.success(`${trackWord} added to "${playlist?.name ?? 'playlist'}"`);
            onClose();
        },
        onError: (e: unknown) => toast.error(e instanceof Error ? e.message : 'Could not add to playlist'),
    });

    const createAndAddMutation = useMutation({
        mutationFn: async ({ name, ids }: { name: string; ids: string[] }) => {
            const created = await playlistService.create({ name, isPublic: false });
            await playlistService.addItems(created.id, ids);
            return created;
        },
        onSuccess: (created) => {
            queryClient.invalidateQueries({ queryKey: ['playlists'] });
            const trackWord = mediaItemIds.length === 1 ? 'Track' : `${mediaItemIds.length} tracks`;
            toast.success(`Created "${created.name}" with ${trackWord.toLowerCase()}`);
            onClose();
        },
        onError: (e: unknown) => toast.error(e instanceof Error ? e.message : 'Could not create playlist'),
    });

    const handleCreate = (e: React.FormEvent) => {
        e.preventDefault();
        const trimmed = newName.trim();
        if (!trimmed) return;
        createAndAddMutation.mutate({ name: trimmed, ids: mediaItemIds });
    };

    const isPending = addMutation.isPending || createAndAddMutation.isPending;

    const verticalClass = placement === 'up' ? 'bottom-full mb-2' : 'top-full mt-2';
    const horizontalClass = align === 'left' ? 'left-0' : 'right-0';

    return (
        <div
            ref={containerRef}
            role="menu"
            aria-label="Add to playlist"
            className={`absolute ${horizontalClass} ${verticalClass} z-50 w-72 bg-[#1a1a1a] border border-white/10 rounded-xl shadow-2xl overflow-hidden`}
        >
            <div className="px-4 py-3 border-b border-white/5">
                <div className="text-sm font-semibold text-white">Add to playlist</div>
                <div className="text-xs text-gray-500 mt-0.5">
                    {mediaItemIds.length} {mediaItemIds.length === 1 ? 'track' : 'tracks'}
                </div>
            </div>

            {showCreate ? (
                <form onSubmit={handleCreate} className="p-3">
                    <input
                        autoFocus
                        type="text"
                        value={newName}
                        onChange={(e) => setNewName(e.target.value)}
                        maxLength={120}
                        placeholder="New playlist name"
                        className="w-full bg-black/30 border border-white/10 rounded-lg px-3 py-2 text-sm text-white placeholder-gray-600 focus:outline-none focus:border-primary mb-2"
                    />
                    <div className="flex gap-2">
                        <button
                            type="button"
                            onClick={() => { setShowCreate(false); setNewName(''); }}
                            className="flex-1 px-3 py-2 text-xs text-gray-300 hover:bg-white/5 rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 min-h-[36px]"
                        >
                            Back
                        </button>
                        <button
                            type="submit"
                            disabled={!newName.trim() || isPending}
                            className="flex-1 px-3 py-2 text-xs bg-primary hover:bg-primary/90 text-white rounded font-medium flex items-center justify-center gap-1.5 disabled:opacity-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 min-h-[36px]"
                        >
                            {isPending && <Loader2 className="w-3 h-3 animate-spin" />}
                            Create
                        </button>
                    </div>
                </form>
            ) : (
                <>
                    <div className="max-h-72 overflow-y-auto py-1">
                        {isLoading ? (
                            <div className="py-6 text-center text-xs text-gray-500">Loading…</div>
                        ) : ownPlaylists.length === 0 ? (
                            <div className="py-6 px-4 text-center text-xs text-gray-500">
                                You don't have any playlists yet. Create one below.
                            </div>
                        ) : (
                            ownPlaylists.map(p => (
                                <button
                                    key={p.id}
                                    type="button"
                                    role="menuitem"
                                    onClick={() => addMutation.mutate({ playlistId: p.id, ids: mediaItemIds })}
                                    disabled={isPending}
                                    className="w-full flex items-center gap-3 px-4 py-2.5 text-left hover:bg-white/5 focus-visible:bg-white/5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-blue-400 transition-colors min-h-[44px] disabled:opacity-50"
                                >
                                    <ListMusic className="w-4 h-4 text-gray-400 shrink-0" />
                                    <div className="flex-1 min-w-0">
                                        <div className="text-sm text-white truncate">{p.name}</div>
                                        <div className="text-xs text-gray-500 flex items-center gap-1.5">
                                            {p.isPublic ? <Globe className="w-3 h-3" /> : <Lock className="w-3 h-3" />}
                                            {p.itemCount} {p.itemCount === 1 ? 'track' : 'tracks'}
                                        </div>
                                    </div>
                                    {addMutation.isPending && addMutation.variables?.playlistId === p.id && (
                                        <Loader2 className="w-4 h-4 animate-spin text-primary shrink-0" />
                                    )}
                                </button>
                            ))
                        )}
                    </div>
                    <div className="border-t border-white/5">
                        <button
                            type="button"
                            onClick={() => setShowCreate(true)}
                            className="w-full flex items-center gap-3 px-4 py-3 text-left text-primary hover:bg-white/5 focus-visible:bg-white/5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-blue-400 transition-colors min-h-[44px]"
                        >
                            <Plus className="w-4 h-4" />
                            <span className="text-sm font-medium">New playlist…</span>
                        </button>
                    </div>
                </>
            )}
        </div>
    );
}
