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
 * positioning anchors against the trigger, and marks the trigger button with
 * `data-add-to-playlist-trigger`. The close-on-outside-pointerdown handler
 * skips that element — otherwise pointerdown closed the menu and the same
 * gesture's click re-toggled it open, so the trigger could never dismiss it.
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

    // Manual playlists only. A smart playlist's tracks come from its rules, so the
    // server rejects an add outright — listing one here would offer a row that
    // can only ever fail.
    const ownPlaylists = playlists.filter(p => p.isOwner && p.kind !== 'Smart');

    // First run: no playlists to pick from, so drop straight into the create
    // form instead of an empty list plus a "create one below" hint.
    const noPlaylistsYet = !isLoading && ownPlaylists.length === 0;
    const showCreateForm = showCreate || noPlaylistsYet;

    // Click-outside + Escape to close.
    useEffect(() => {
        const onPointer = (e: PointerEvent) => {
            const target = e.target as HTMLElement;
            // The trigger toggles on click; closing here too made that click
            // instantly reopen the menu.
            if (target.closest('[data-add-to-playlist-trigger]')) return;
            if (containerRef.current && !containerRef.current.contains(target)) {
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
            {/* Deliberately styled as a caption, not a row: this header used to be
                the boldest thing in the panel and users clicked IT as "the button",
                when the actual actions are the playlist rows below. */}
            <div className="px-4 py-2.5 border-b border-white/5 bg-white/[0.03]">
                <div className="text-[11px] font-semibold uppercase tracking-wider text-gray-500">
                    {showCreateForm
                        ? 'New playlist'
                        : `Save ${mediaItemIds.length === 1 ? 'track' : `${mediaItemIds.length} tracks`} to…`}
                </div>
            </div>

            {showCreateForm ? (
                <form onSubmit={handleCreate} className="p-3">
                    {noPlaylistsYet && (
                        <p className="text-xs text-gray-400 mb-2">
                            Name your first playlist — the {mediaItemIds.length === 1 ? 'track' : 'tracks'} will be added to it.
                        </p>
                    )}
                    <input
                        autoFocus
                        type="text"
                        value={newName}
                        onChange={(e) => setNewName(e.target.value)}
                        maxLength={120}
                        placeholder="Playlist name"
                        className="w-full bg-black/30 border border-white/10 rounded-lg px-3 py-2 text-sm text-white placeholder-gray-600 focus:outline-none focus:border-primary mb-2"
                    />
                    <div className="flex gap-2">
                        {/* With zero playlists there is no list to go back to. */}
                        {!noPlaylistsYet && (
                            <button
                                type="button"
                                onClick={() => { setShowCreate(false); setNewName(''); }}
                                className="flex-1 px-3 py-2 text-xs text-gray-300 hover:bg-white/5 rounded cursor-pointer focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 min-h-[36px]"
                            >
                                Back
                            </button>
                        )}
                        <button
                            type="submit"
                            disabled={!newName.trim() || isPending}
                            className="flex-1 px-3 py-2 text-xs bg-primary hover:bg-primary/90 text-white rounded font-medium flex items-center justify-center gap-1.5 disabled:opacity-50 cursor-pointer focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 min-h-[36px]"
                        >
                            {isPending && <Loader2 className="w-3 h-3 animate-spin" />}
                            Create &amp; add
                        </button>
                    </div>
                </form>
            ) : (
                <>
                    <div className="max-h-72 overflow-y-auto py-1">
                        {isLoading ? (
                            <div className="py-6 text-center text-xs text-gray-500">Loading…</div>
                        ) : (
                            // Tailwind v4 preflight gives <button> cursor:default, which
                            // made these rows read as inert text — cursor-pointer is
                            // load-bearing, not cosmetic. The trailing "+ Add" appears on
                            // hover/focus so each row announces what clicking does.
                            ownPlaylists.map(p => (
                                <button
                                    key={p.id}
                                    type="button"
                                    role="menuitem"
                                    onClick={() => addMutation.mutate({ playlistId: p.id, ids: mediaItemIds })}
                                    disabled={isPending}
                                    className="group/row w-full flex items-center gap-3 px-4 py-2.5 text-left cursor-pointer hover:bg-white/10 focus-visible:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-blue-400 transition-colors min-h-[44px] disabled:opacity-50"
                                >
                                    <ListMusic className="w-4 h-4 text-gray-400 shrink-0" />
                                    <div className="flex-1 min-w-0">
                                        <div className="text-sm text-white truncate">{p.name}</div>
                                        <div className="text-xs text-gray-500 flex items-center gap-1.5">
                                            {p.isPublic ? <Globe className="w-3 h-3" /> : <Lock className="w-3 h-3" />}
                                            {p.itemCount} {p.itemCount === 1 ? 'track' : 'tracks'}
                                        </div>
                                    </div>
                                    {addMutation.isPending && addMutation.variables?.playlistId === p.id ? (
                                        <Loader2 className="w-4 h-4 animate-spin text-primary shrink-0" />
                                    ) : (
                                        <span className="shrink-0 inline-flex items-center gap-1 text-xs font-medium text-primary opacity-0 group-hover/row:opacity-100 group-focus-visible/row:opacity-100 transition-opacity">
                                            <Plus className="w-3.5 h-3.5" />
                                            Add
                                        </span>
                                    )}
                                </button>
                            ))
                        )}
                    </div>
                    <div className="border-t border-white/5">
                        <button
                            type="button"
                            onClick={() => setShowCreate(true)}
                            className="w-full flex items-center gap-3 px-4 py-3 text-left text-primary cursor-pointer hover:bg-white/5 focus-visible:bg-white/5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-blue-400 transition-colors min-h-[44px]"
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
