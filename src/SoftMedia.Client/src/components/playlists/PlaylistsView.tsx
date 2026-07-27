import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { motion } from 'framer-motion';
import { ListMusic, Plus, Globe, Lock, User as UserIcon, Loader2, Play, Sparkles, Upload } from 'lucide-react';
import { toast } from 'sonner';
import { playlistService, type PlaylistSummary } from '../../services/playlistService';
import { PLAYLIST_ORIGIN_PARAM } from '../../lib/backNavigation';
import { formatRelativeTime, cn } from '../../lib/utils';
import { PlaylistCover } from './PlaylistCover';
import { SMART_PLAYLIST_PRESETS, type SmartPlaylistPreset } from '../../lib/smartPlaylistPresets';

interface PlaylistsViewProps {
    /**
     * The Music library hosting this tab. Stamped onto each playlist link so the
     * detail page's "All playlists" returns to THIS library rather than guessing
     * when a server has several Music libraries.
     */
    libraryId?: string;
    /**
     * Text from the library's FilterBar. Filtering happens client-side over the
     * already-loaded list rather than through /playlists/search: this view holds
     * the full set, so narrowing it is instant and needs no round-trip.
     */
    searchQuery?: string;
}

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
export default function PlaylistsView({ libraryId, searchQuery }: PlaylistsViewProps = {}) {
    const queryClient = useQueryClient();
    const [showCreate, setShowCreate] = useState(false);
    const [newName, setNewName] = useState('');
    const [newDescription, setNewDescription] = useState('');
    const [newIsPublic, setNewIsPublic] = useState(false);
    // null = a normal, hand-curated playlist. Otherwise the chosen preset's id.
    const [presetId, setPresetId] = useState<string | null>(null);

    const selectedPreset = SMART_PLAYLIST_PRESETS.find(p => p.id === presetId) ?? null;

    const resetCreateForm = () => {
        setShowCreate(false);
        setNewName('');
        setNewDescription('');
        setNewIsPublic(false);
        setPresetId(null);
    };

    /**
     * Picking a preset seeds the name, since "Most Played" is almost always what
     * the playlist should be called — but only when the user hasn't typed their
     * own, so switching presets never discards a name they chose.
     */
    const choosePreset = (preset: SmartPlaylistPreset | null) => {
        setPresetId(preset?.id ?? null);
        if (preset && !newName.trim()) setNewName(preset.suggestedName);
        // Smart playlists cannot be shared; drop a toggle set before the switch.
        if (preset) setNewIsPublic(false);
    };

    const { data: playlists = [], isLoading } = useQuery<PlaylistSummary[]>({
        queryKey: ['playlists'],
        queryFn: playlistService.list,
    });

    const createMutation = useMutation({
        mutationFn: playlistService.create,
        onSuccess: (created) => {
            queryClient.invalidateQueries({ queryKey: ['playlists'] });
            toast.success(`Playlist "${created.name}" created`);
            resetCreateForm();
        },
        onError: (e: unknown) => toast.error(e instanceof Error ? e.message : 'Could not create playlist'),
    });

    /**
     * Import reads the file in the browser and posts its text — the server never
     * receives or opens a path, it only matches the lines against rows already in
     * the library.
     */
    const importMutation = useMutation({
        mutationFn: ({ content, name }: { content: string; name?: string }) =>
            playlistService.importM3u(content, name),
        onSuccess: (result) => {
            queryClient.invalidateQueries({ queryKey: ['playlists'] });
            // Always report what was skipped. An import that quietly drops half a
            // playlist and says "done" is the failure mode worth avoiding.
            if (result.unmatchedCount > 0) {
                toast.success(
                    `Imported ${result.matchedCount} of ${result.matchedCount + result.unmatchedCount} tracks into "${result.playlist.name}". ` +
                    `${result.unmatchedCount} weren't found in your library.`
                );
            } else {
                toast.success(`Imported ${result.matchedCount} tracks into "${result.playlist.name}"`);
            }
        },
        onError: (e: unknown) => toast.error(e instanceof Error ? e.message : 'Could not import playlist'),
    });

    const handleImportFile = async (e: React.ChangeEvent<HTMLInputElement>) => {
        const file = e.target.files?.[0];
        // Reset immediately so picking the same file twice still fires a change.
        e.target.value = '';
        if (!file) return;

        const content = await file.text();
        importMutation.mutate({ content, name: file.name.replace(/\.(m3u8?|txt)$/i, '') });
    };

    const handleCreate = (e: React.FormEvent) => {
        e.preventDefault();
        const trimmed = newName.trim();
        if (!trimmed) return;
        createMutation.mutate({
            name: trimmed,
            description: newDescription.trim() || undefined,
            isPublic: selectedPreset ? false : newIsPublic,
            rules: selectedPreset?.rules,
        });
    };

    // Name and description, matching what the global playlist search matches on —
    // a query that finds a playlist in the top bar should find it here too.
    const normalizedQuery = (searchQuery ?? '').trim().toLowerCase();
    const matching = normalizedQuery
        ? playlists.filter(p =>
            p.name.toLowerCase().includes(normalizedQuery)
            || (p.description ?? '').toLowerCase().includes(normalizedQuery))
        : playlists;

    const ownPlaylists = matching.filter(p => p.isOwner);
    const sharedPlaylists = matching.filter(p => !p.isOwner);
    const isFiltering = normalizedQuery.length > 0;

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
                <div className="flex items-center gap-2">
                    {/* A label wrapping a hidden input: the native file picker with
                        button styling, and it stays keyboard-reachable. */}
                    <label
                        className="flex items-center gap-2 px-4 py-2.5 bg-white/5 hover:bg-white/10 text-gray-200 rounded-lg font-medium transition-colors cursor-pointer min-h-[44px] text-sm focus-within:ring-2 focus-within:ring-blue-400"
                        title="Create a playlist from an M3U file"
                    >
                        {importMutation.isPending
                            ? <Loader2 className="w-4 h-4 animate-spin" />
                            : <Upload className="w-4 h-4" />}
                        Import
                        <input
                            type="file"
                            accept=".m3u,.m3u8,audio/x-mpegurl,text/plain"
                            onChange={handleImportFile}
                            disabled={importMutation.isPending}
                            className="sr-only"
                        />
                    </label>

                    <button
                        type="button"
                        onClick={() => setShowCreate(true)}
                        className="flex items-center gap-2 px-4 py-2.5 bg-primary hover:bg-primary/90 text-white rounded-lg font-medium transition-all shadow-lg shadow-primary/20 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 min-h-[44px] text-sm"
                    >
                        <Plus className="w-4 h-4" />
                        New Playlist
                    </button>
                </div>
            </div>

            {isLoading ? (
                <div className="flex justify-center py-12 text-gray-400">
                    <Loader2 className="w-6 h-6 animate-spin" />
                </div>
            ) : (
                <div className="space-y-10">
                    <PlaylistSection
                        heading="Your Playlists"
                        playlists={ownPlaylists}
                        libraryId={libraryId}
                        // While filtering, "none of yours match" is the right
                        // message — the create-your-first prompt would be wrong
                        // for someone who simply mistyped a name.
                        emptyState={isFiltering
                            ? <NoMatches query={searchQuery ?? ''} />
                            : <NoPlaylistsYet onCreate={() => setShowCreate(true)} />}
                    />

                    {/* The shared section stays hidden when the server has none,
                        but while filtering it appears with its own empty message —
                        otherwise a query matching only your own playlists silently
                        drops the heading and looks like shared ones vanished. */}
                    {(sharedPlaylists.length > 0 || (isFiltering && playlists.some(p => !p.isOwner))) && (
                        <PlaylistSection
                            heading="Shared on this server"
                            playlists={sharedPlaylists}
                            libraryId={libraryId}
                            emptyState={<NoMatches query={searchQuery ?? ''} />}
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
                            Build it yourself, or let one keep itself up to date.
                        </p>

                        <form onSubmit={handleCreate} className="space-y-4">
                            {/* Kind picker. The empty-list case aside, most new playlists
                                are manual, so that stays the default and the automatic
                                options sit beside it rather than behind a mode switch. */}
                            <div>
                                <span className="block text-sm text-gray-400 mb-2">Type</span>
                                <div className="grid grid-cols-2 gap-2">
                                    <button
                                        type="button"
                                        aria-pressed={presetId === null}
                                        onClick={() => choosePreset(null)}
                                        className={cn(
                                            'flex items-center gap-2 px-3 py-2.5 rounded-lg text-sm transition-colors min-h-[44px] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400',
                                            presetId === null
                                                ? 'bg-primary/20 text-white ring-1 ring-primary/50'
                                                : 'bg-white/5 text-gray-300 hover:bg-white/10'
                                        )}
                                    >
                                        <ListMusic className="w-4 h-4" />
                                        Manual
                                    </button>
                                    <div
                                        className={cn(
                                            'flex items-center gap-2 px-3 py-2.5 rounded-lg text-sm min-h-[44px]',
                                            presetId !== null
                                                ? 'bg-primary/20 text-white ring-1 ring-primary/50'
                                                : 'bg-white/5 text-gray-500'
                                        )}
                                    >
                                        <Sparkles className="w-4 h-4" />
                                        Automatic
                                    </div>
                                </div>

                                <div className="mt-2 grid grid-cols-1 gap-1.5">
                                    {SMART_PLAYLIST_PRESETS.map(preset => (
                                        <button
                                            key={preset.id}
                                            type="button"
                                            aria-pressed={presetId === preset.id}
                                            onClick={() => choosePreset(presetId === preset.id ? null : preset)}
                                            className={cn(
                                                'w-full text-left px-3 py-2 rounded-lg transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400',
                                                presetId === preset.id
                                                    ? 'bg-primary/15 ring-1 ring-primary/40'
                                                    : 'bg-white/[0.03] hover:bg-white/[0.07]'
                                            )}
                                        >
                                            <div className="text-sm text-white">{preset.label}</div>
                                            <div className="text-xs text-gray-500">{preset.hint}</div>
                                        </button>
                                    ))}
                                </div>
                            </div>

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

                            <div>
                                <label className="block text-sm text-gray-400 mb-2">
                                    Description <span className="text-gray-600">(optional)</span>
                                </label>
                                <textarea
                                    value={newDescription}
                                    onChange={(e) => setNewDescription(e.target.value)}
                                    maxLength={500}
                                    rows={2}
                                    className="w-full bg-black/30 border border-white/10 rounded-lg px-4 py-2.5 text-sm text-white placeholder-gray-600 focus:outline-none focus:border-primary resize-none"
                                    placeholder="What's this playlist for?"
                                />
                            </div>

                            {/* An automatic playlist is defined by the owner's own
                                favourites and listening, so it cannot be shared — the
                                server rejects it too. Explaining that in place beats
                                offering a toggle that would fail on submit. */}
                            {selectedPreset ? (
                                <div className="w-full flex items-center gap-3 px-4 py-3 bg-white/5 rounded-lg min-h-[44px]">
                                    <Lock className="w-4 h-4 text-gray-400 shrink-0" />
                                    <div>
                                        <div className="text-white text-sm font-medium">Private</div>
                                        <div className="text-xs text-gray-500">
                                            Automatic playlists are built from your own listening, so they stay private.
                                        </div>
                                    </div>
                                </div>
                            ) : (
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
                            )}

                            <div className="flex gap-2 pt-2">
                                <button
                                    type="button"
                                    onClick={resetCreateForm}
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

function PlaylistSection({ heading, playlists, libraryId, emptyState }: {
    heading: string;
    playlists: PlaylistSummary[];
    libraryId?: string;
    /** Rendered in place of the grid when the section has no playlists. */
    emptyState?: React.ReactNode;
}) {
    return (
        <section>
            <h3 className="text-sm font-semibold text-gray-400 uppercase tracking-wider mb-4">{heading}</h3>
            {playlists.length === 0 ? (
                emptyState ?? null
            ) : (
                <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5 2xl:grid-cols-6 gap-4">
                    {playlists.map(p => <PlaylistCard key={p.id} playlist={p} libraryId={libraryId} />)}
                </div>
            )}
        </section>
    );
}

/** Section-level "your filter matched nothing here", distinct from having none. */
function NoMatches({ query }: { query: string }) {
    return (
        <div className="bg-white/5 border border-white/10 rounded-xl p-8 text-center text-sm text-gray-400">
            No playlists match “{query.trim()}”.
        </div>
    );
}

/**
 * First-run state. Styled after the hero's empty state (soft radial wash, tilted
 * glass tile) rather than the plain bordered box it replaces — this is the first
 * thing a new user sees on the tab, and it carries the primary action rather
 * than pointing at a button elsewhere on the page.
 */
function NoPlaylistsYet({ onCreate }: { onCreate: () => void }) {
    return (
        <div className="relative overflow-hidden rounded-2xl border border-white/10 bg-gradient-to-br from-violet-950/30 via-white/[0.02] to-transparent px-6 py-14 text-center">
            <div className="absolute inset-0 bg-[radial-gradient(circle_at_center,_var(--tw-gradient-stops))] from-primary/10 via-transparent to-transparent" />
            <div className="relative flex flex-col items-center gap-5">
                <div className="w-20 h-20 rounded-3xl bg-white/5 border border-white/10 backdrop-blur-xl flex items-center justify-center shadow-2xl rotate-3 transform-gpu">
                    <ListMusic className="w-9 h-9 text-primary/70" />
                </div>
                <div className="space-y-2">
                    <h4 className="text-xl font-bold text-white">Build your first playlist</h4>
                    <p className="text-sm text-gray-400 max-w-sm mx-auto leading-relaxed">
                        Collect tracks from across your music library into a list you can play
                        end to end. Playlists stay private unless you share them.
                    </p>
                </div>
                <button
                    type="button"
                    onClick={onCreate}
                    className="inline-flex items-center gap-2 px-5 py-2.5 bg-primary hover:bg-primary/90 text-white rounded-lg font-medium transition-all shadow-lg shadow-primary/20 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 min-h-[44px] text-sm"
                >
                    <Plus className="w-4 h-4" />
                    New Playlist
                </button>
            </div>
        </div>
    );
}

function PlaylistCard({ playlist, libraryId }: { playlist: PlaylistSummary; libraryId?: string }) {
    // The origin rides on the link so "All playlists" can come back here.
    const href = libraryId
        ? `/playlists/${playlist.id}?${PLAYLIST_ORIGIN_PARAM}=${encodeURIComponent(libraryId)}`
        : `/playlists/${playlist.id}`;

    const updated = formatRelativeTime(playlist.updatedAt);

    return (
        <Link
            to={href}
            className="group bg-white/5 hover:bg-white/10 focus-visible:bg-white/10 focus-visible:ring-2 focus-visible:ring-blue-400 focus-visible:outline-none border border-white/10 rounded-xl p-4 transition-all flex flex-col gap-3 min-h-[44px]"
        >
            {/* Artwork leads, as it does on every other card in the app. The play
                badge is the hover affordance the old icon tile had no room for. */}
            <div className="relative">
                <PlaylistCover
                    coverPaths={playlist.coverImagePaths}
                    className="w-full aspect-square rounded-lg shadow-lg"
                    iconClassName="w-1/3 h-1/3"
                />
                <div className="absolute inset-0 rounded-lg bg-gradient-to-t from-black/40 to-transparent opacity-0 group-hover:opacity-100 transition-opacity" />
                <div className="absolute bottom-2 right-2 w-10 h-10 rounded-full bg-primary text-white shadow-lg shadow-black/40 flex items-center justify-center translate-y-1 opacity-0 group-hover:opacity-100 group-hover:translate-y-0 group-focus-visible:opacity-100 group-focus-visible:translate-y-0 transition-all">
                    <Play className="w-4 h-4 fill-current" />
                </div>
            </div>

            <div className="min-w-0">
                <div className="font-semibold text-white truncate flex items-center gap-1.5">
                    {playlist.kind === 'Smart' && (
                        <Sparkles
                            className="w-3.5 h-3.5 text-primary shrink-0"
                            aria-label="Automatic playlist"
                        />
                    )}
                    <span className="truncate">{playlist.name}</span>
                </div>
                <div className="text-xs text-gray-400 mt-1 flex items-center gap-1.5 flex-wrap">
                    {playlist.isPublic
                        ? <span className="inline-flex items-center gap-1"><Globe className="w-3 h-3" /> Public</span>
                        : <span className="inline-flex items-center gap-1"><Lock className="w-3 h-3" /> Private</span>}
                    <span aria-hidden="true">·</span>
                    <span>{playlist.itemCount} {playlist.itemCount === 1 ? 'track' : 'tracks'}</span>
                    {updated && (
                        <>
                            <span aria-hidden="true">·</span>
                            <span>{updated}</span>
                        </>
                    )}
                </div>
                {!playlist.isOwner && (
                    <div className="text-xs text-gray-500 mt-1 flex items-center gap-1.5 truncate">
                        <UserIcon className="w-3 h-3 shrink-0" />
                        Shared by {playlist.ownerUsername}
                    </div>
                )}
            </div>
        </Link>
    );
}
