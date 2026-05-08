import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { motion } from 'framer-motion';
import { ListMusic, Plus, Globe, Lock, User as UserIcon, Loader2 } from 'lucide-react';
import { toast } from 'sonner';
import { playlistService, type PlaylistSummary } from '../../services/playlistService';

/**
 * Reusable playlist grid + create modal. Embedded inside the Music library
 * page when the user picks the "Playlists" view-mode tab. Previously this
 * lived as a standalone /playlists page, but playlists are music-only in v1
 * so a global page misrepresented their scope.
 *
 * Two sections:
 *   - "Your Playlists" (user-owned)
 *   - "Shared on this server" (public, owned by other users) — only renders
 *     when at least one such playlist exists.
 */
export default function PlaylistsView() {
    const queryClient = useQueryClient();
    const [showCreate, setShowCreate] = useState(false);
    const [newName, setNewName] = useState('');
    const [newIsPublic, setNewIsPublic] = useState(false);

    const { data: playlists = [], isLoading } = useQuery<PlaylistSummary[]>({
        queryKey: ['playlists'],
        queryFn: playlistService.list,
    });

    const createMutation = useMutation({
        mutationFn: playlistService.create,
        onSuccess: (created) => {
            queryClient.invalidateQueries({ queryKey: ['playlists'] });
            toast.success(`Playlist "${created.name}" created`);
            setShowCreate(false);
            setNewName('');
            setNewIsPublic(false);
        },
        onError: (e: unknown) => toast.error(e instanceof Error ? e.message : 'Could not create playlist'),
    });

    const handleCreate = (e: React.FormEvent) => {
        e.preventDefault();
        const trimmed = newName.trim();
        if (!trimmed) return;
        createMutation.mutate({ name: trimmed, isPublic: newIsPublic });
    };

    const ownPlaylists = playlists.filter(p => p.isOwner);
    const sharedPlaylists = playlists.filter(p => !p.isOwner);

    return (
        <div className="px-8 pt-8 pb-10">
            {/* Header row with the New Playlist action */}
            <div className="flex items-center justify-between mb-6">
                <div>
                    <h2 className="text-xl font-semibold text-white flex items-center gap-2">
                        <ListMusic className="w-5 h-5 text-primary" />
                        Playlists
                    </h2>
                    <p className="text-sm text-gray-400 mt-0.5">
                        Your music playlists. New playlists are private by default.
                    </p>
                </div>
                <button
                    type="button"
                    onClick={() => setShowCreate(true)}
                    className="flex items-center gap-2 px-4 py-2.5 bg-primary hover:bg-primary/90 text-white rounded-lg font-medium transition-all shadow-lg shadow-primary/20 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 min-h-[44px] text-sm"
                >
                    <Plus className="w-4 h-4" />
                    New Playlist
                </button>
            </div>

            {isLoading ? (
                <div className="flex justify-center py-12 text-gray-400">
                    <Loader2 className="w-6 h-6 animate-spin" />
                </div>
            ) : (
                <div className="space-y-10">
                    <PlaylistSection
                        heading="Your Playlists"
                        empty="You haven't created any playlists yet. Click 'New Playlist' to start one."
                        playlists={ownPlaylists}
                    />

                    {sharedPlaylists.length > 0 && (
                        <PlaylistSection
                            heading="Shared on this server"
                            empty=""
                            playlists={sharedPlaylists}
                        />
                    )}
                </div>
            )}

            {showCreate && (
                <div className="fixed inset-0 bg-black/60 backdrop-blur-sm flex items-center justify-center z-50 p-4">
                    <motion.div
                        initial={{ opacity: 0, scale: 0.95 }}
                        animate={{ opacity: 1, scale: 1 }}
                        className="bg-[#1a1a1a] rounded-xl p-6 w-full max-w-md border border-white/10 shadow-2xl"
                    >
                        <h2 className="text-xl font-bold mb-2">New Playlist</h2>
                        <p className="text-sm text-gray-400 mb-5">
                            Name your playlist. You can rename it or change visibility later.
                        </p>

                        <form onSubmit={handleCreate} className="space-y-4">
                            <div>
                                <label className="block text-sm text-gray-400 mb-2">Name</label>
                                <input
                                    type="text"
                                    value={newName}
                                    onChange={(e) => setNewName(e.target.value)}
                                    maxLength={120}
                                    autoFocus
                                    className="w-full bg-black/30 border border-white/10 rounded-lg px-4 py-2.5 text-white placeholder-gray-600 focus:outline-none focus:border-primary"
                                    placeholder="e.g. Saturday Night"
                                    required
                                />
                            </div>

                            <button
                                type="button"
                                role="switch"
                                aria-checked={newIsPublic}
                                onClick={() => setNewIsPublic(v => !v)}
                                className="w-full flex items-center justify-between gap-3 px-4 py-3 bg-white/5 hover:bg-white/10 focus-visible:bg-white/10 focus-visible:ring-2 focus-visible:ring-blue-400 focus-visible:outline-none rounded-lg transition-colors min-h-[44px]"
                            >
                                <div className="flex items-center gap-3 text-left">
                                    {newIsPublic ? <Globe className="w-4 h-4 text-primary" /> : <Lock className="w-4 h-4 text-gray-400" />}
                                    <div>
                                        <div className="text-white text-sm font-medium">
                                            {newIsPublic ? 'Public' : 'Private'}
                                        </div>
                                        <div className="text-xs text-gray-500">
                                            {newIsPublic ? 'Visible to other users on this server.' : 'Only you can see this playlist.'}
                                        </div>
                                    </div>
                                </div>
                                <div className={`w-10 h-5 rounded-full relative transition-colors ${newIsPublic ? 'bg-primary' : 'bg-white/20'}`}>
                                    <div className={`absolute top-0.5 w-4 h-4 rounded-full bg-white transition-all ${newIsPublic ? 'left-5' : 'left-0.5'}`} />
                                </div>
                            </button>

                            <div className="flex gap-2 pt-2">
                                <button
                                    type="button"
                                    onClick={() => { setShowCreate(false); setNewName(''); setNewIsPublic(false); }}
                                    className="flex-1 px-4 py-2.5 rounded-lg text-gray-300 hover:bg-white/5 focus-visible:bg-white/5 focus-visible:ring-2 focus-visible:ring-blue-400 focus-visible:outline-none transition-colors min-h-[44px]"
                                >
                                    Cancel
                                </button>
                                <button
                                    type="submit"
                                    disabled={!newName.trim() || createMutation.isPending}
                                    className="flex-1 px-4 py-2.5 rounded-lg bg-primary hover:bg-primary/90 text-white font-medium transition-colors disabled:opacity-50 disabled:cursor-not-allowed flex items-center justify-center gap-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 min-h-[44px]"
                                >
                                    {createMutation.isPending && <Loader2 className="w-4 h-4 animate-spin" />}
                                    Create
                                </button>
                            </div>
                        </form>
                    </motion.div>
                </div>
            )}
        </div>
    );
}

function PlaylistSection({ heading, empty, playlists }: { heading: string; empty: string; playlists: PlaylistSummary[] }) {
    return (
        <section>
            <h3 className="text-sm font-semibold text-gray-400 uppercase tracking-wider mb-4">{heading}</h3>
            {playlists.length === 0 ? (
                <div className="bg-white/5 border border-white/10 rounded-xl p-8 text-center text-gray-400 text-sm">
                    {empty || 'Nothing here yet.'}
                </div>
            ) : (
                <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
                    {playlists.map(p => <PlaylistCard key={p.id} playlist={p} />)}
                </div>
            )}
        </section>
    );
}

function PlaylistCard({ playlist }: { playlist: PlaylistSummary }) {
    return (
        <Link
            to={`/playlists/${playlist.id}`}
            className="group bg-white/5 hover:bg-white/10 focus-visible:bg-white/10 focus-visible:ring-2 focus-visible:ring-blue-400 focus-visible:outline-none border border-white/10 rounded-xl p-5 transition-all flex flex-col gap-3 min-h-[44px]"
        >
            <div className="flex items-start gap-3">
                <div className="w-12 h-12 bg-brand-gradient rounded-lg flex items-center justify-center flex-shrink-0">
                    <ListMusic className="w-6 h-6 text-white" />
                </div>
                <div className="flex-1 min-w-0">
                    <div className="font-semibold text-white truncate">{playlist.name}</div>
                    <div className="text-xs text-gray-400 mt-0.5 flex items-center gap-2">
                        {playlist.isPublic
                            ? <><Globe className="w-3 h-3" /> Public</>
                            : <><Lock className="w-3 h-3" /> Private</>}
                        <span>·</span>
                        <span>{playlist.itemCount} {playlist.itemCount === 1 ? 'track' : 'tracks'}</span>
                    </div>
                </div>
            </div>
            {!playlist.isOwner && (
                <div className="text-xs text-gray-500 flex items-center gap-1.5">
                    <UserIcon className="w-3 h-3" />
                    Shared by {playlist.ownerUsername}
                </div>
            )}
        </Link>
    );
}
