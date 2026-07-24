import { useEffect, useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { isAxiosError } from 'axios';
import { Lock, Unlock, Wand2, X, Loader2, Edit3, RefreshCw } from 'lucide-react';
import { toast } from 'sonner';
import { adminService, type MetadataSearchCandidate } from '../../services/adminService';
import { type MediaItem } from '../../types';

/**
 * Admin-only "Fix Match" control (P3-WI-003). Collapsed to a single icon button on
 * the detail page so it takes no vertical space; clicking it opens a modal with the
 * full interface. Three modes inside the modal:
 *   - Re-search a metadata provider, pick a candidate, apply (auto-locks).
 *   - Manually edit fields (title/overview/year/poster), saves with auto-lock.
 *   - Unlock to re-enable auto-refresh.
 * When the item's metadata is locked the trigger icon turns into an amber lock so
 * the lock status stays glanceable without opening the modal.
 */
export function FixMatchCard({ item }: { item: MediaItem }) {
    const queryClient = useQueryClient();
    const [open, setOpen] = useState(false);
    const [mode, setMode] = useState<'idle' | 'search' | 'edit'>('idle');
    const [query, setQuery] = useState('');
    const [year, setYear] = useState<string>('');
    const [candidates, setCandidates] = useState<MetadataSearchCandidate[]>([]);
    const [searched, setSearched] = useState(false);

    // Manual edit form state
    const [editTitle, setEditTitle] = useState('');
    const [editOverview, setEditOverview] = useState('');
    const [editYear, setEditYear] = useState<string>('');
    const [editPoster, setEditPoster] = useState('');

    const invalidate = () => queryClient.invalidateQueries({ queryKey: ['media', item.id] });

    const closeModal = () => {
        setOpen(false);
        setMode('idle');
        setCandidates([]);
        setSearched(false);
    };

    // Escape closes the modal, matching common dialog behaviour.
    useEffect(() => {
        if (!open) return;
        const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') closeModal(); };
        window.addEventListener('keydown', onKey);
        return () => window.removeEventListener('keydown', onKey);
    }, [open]);

    const searchMutation = useMutation({
        mutationFn: () => adminService.searchMatch(item.id, query.trim(), year.trim() ? Number(year) : null),
        onSuccess: (res) => { setCandidates(res); setSearched(true); },
        onError: () => toast.error('Search failed'),
    });

    const applyMutation = useMutation({
        mutationFn: (c: MetadataSearchCandidate) => adminService.applyMatch(item.id, c.providerName, c.providerItemId),
        onSuccess: () => {
            toast.success('Match applied — item locked');
            closeModal();
            invalidate();
        },
        onError: () => toast.error('Failed to apply match'),
    });

    const editMutation = useMutation({
        mutationFn: () => adminService.manualEditMatch(item.id, {
            title: editTitle.trim() || undefined,
            overview: editOverview.trim() || undefined,
            year: editYear.trim() ? Number(editYear) : undefined,
            posterUrl: editPoster.trim() || undefined,
        }),
        onSuccess: () => {
            toast.success('Saved — item locked');
            closeModal();
            invalidate();
        },
        onError: () => toast.error('Save failed'),
    });

    const unlockMutation = useMutation({
        mutationFn: () => adminService.unlockMatch(item.id),
        onSuccess: () => { toast.success('Unlocked'); invalidate(); },
        onError: () => toast.error('Unlock failed'),
    });

    // SR-WI-036 — per-item metadata refresh: clears the server's retry-exhausted state and
    // re-queues the item for enrichment. Locked items are rejected server-side with 409.
    const refreshMutation = useMutation({
        mutationFn: () => adminService.refreshMatch(item.id),
        onSuccess: () => {
            toast.success('Metadata refresh queued');
            closeModal();
        },
        onError: (err) => {
            if (isAxiosError(err) && err.response?.status === 409) {
                toast.error('Metadata is locked — unlock it first to refresh');
            } else {
                toast.error('Refresh failed');
            }
        },
    });

    const openEdit = () => {
        setEditTitle(item.title ?? '');
        // The overview shown on the detail page lives in `description` on MediaItem —
        // pre-fill from there so the editor reflects the current local metadata.
        setEditOverview(item.description ?? '');
        setEditYear(item.year != null ? String(item.year) : '');
        setEditPoster(item.posterPath ?? '');
        setMode('edit');
    };

    // Brand blue is supplied as an arbitrary value (#007AFF) rather than `bg-primary`:
    // this project runs Tailwind v4 with the palette declared under :root (not @theme),
    // so the `primary` color utilities are never generated.
    const inputCls = 'w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-base text-white focus:outline-none focus:border-[#007AFF]';
    const btnPrimary = 'inline-flex items-center gap-2 px-5 py-2.5 text-base bg-[#007AFF] hover:bg-[#005BB5] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 text-white rounded-lg disabled:opacity-50';
    const btnGhost = 'inline-flex items-center gap-2 px-5 py-2.5 text-base bg-white/5 hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 text-white rounded-lg';

    return (
        <>
            {/* Collapsed trigger — an icon that sits in the detail page's action row
                alongside Favorite / Watched / Share. Amber lock when metadata is locked. */}
            <button
                type="button"
                onClick={() => { setMode('idle'); setOpen(true); }}
                className="group"
                title={item.metadataLocked ? 'Metadata locked — fix match' : 'Fix match'}
                aria-label={item.metadataLocked ? 'Metadata locked — fix match' : 'Fix match'}
            >
                <div className={`p-3 rounded-full transition-all group-hover:scale-110 active:scale-95 ${
                    item.metadataLocked ? 'bg-amber-500/20 text-amber-400' : 'bg-white/5 hover:bg-white/10 text-white'
                }`}>
                    {item.metadataLocked
                        ? <Lock className="w-5 h-5" />
                        : <Edit3 className="w-5 h-5" />}
                </div>
            </button>

            {open && (
                <div className="fixed inset-0 flex items-center justify-center z-[60] p-4">
                    {/* Click-outside-to-close backdrop as a real <button> (not a <div onClick>) so it is
                        keyboard-focusable and screen-reader-announced. Escape also closes (effect above). */}
                    <button
                        type="button"
                        aria-label="Close dialog"
                        onClick={closeModal}
                        className="absolute inset-0 bg-black/80 backdrop-blur-sm cursor-default focus-visible:outline-none"
                    />
                    <div
                        className="relative bg-[#1a1a1a] rounded-xl p-7 max-w-2xl w-full border border-white/10 shadow-2xl max-h-[88vh] overflow-y-auto"
                        role="dialog"
                        aria-modal="true"
                    >
                        <div className="flex items-center justify-between mb-5">
                            <div className="flex items-center gap-2.5">
                                {item.metadataLocked ? <Lock className="w-5 h-5 text-amber-400" /> : <Wand2 className="w-5 h-5 text-[#007AFF]" />}
                                <h3 className="text-lg font-semibold text-white">
                                    {item.metadataLocked ? 'Metadata locked' : 'Fix match'}
                                </h3>
                                {item.metadataLocked && item.metadataLockedAt && (
                                    <span className="text-sm text-gray-500">since {new Date(item.metadataLockedAt).toLocaleDateString()}</span>
                                )}
                            </div>
                            <button type="button" onClick={closeModal} className="p-2 rounded hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 text-gray-400" aria-label="Close">
                                <X size={20} />
                            </button>
                        </div>

                        {mode === 'idle' && (
                            <div className="space-y-4">
                                <div className="flex flex-wrap items-center gap-3">
                                    <button type="button" onClick={() => { setMode('search'); setQuery(item.title ?? ''); setSearched(false); }} className={btnGhost}>
                                        <Wand2 size={18} /> Re-search
                                    </button>
                                    <button type="button" onClick={openEdit} className={btnGhost}>
                                        <Edit3 size={18} /> Edit fields
                                    </button>
                                    <button type="button" onClick={() => refreshMutation.mutate()} disabled={refreshMutation.isPending} className={btnGhost + ' disabled:opacity-50'}>
                                        {refreshMutation.isPending ? <Loader2 size={18} className="animate-spin" /> : <RefreshCw size={18} />}
                                        Refresh metadata
                                    </button>
                                    {item.metadataLocked && (
                                        <button type="button" onClick={() => unlockMutation.mutate()} disabled={unlockMutation.isPending}
                                            className="inline-flex items-center gap-2 px-5 py-2.5 text-base bg-amber-500/20 hover:bg-amber-500/30 text-amber-300 rounded-lg disabled:opacity-50">
                                            {unlockMutation.isPending ? <Loader2 size={18} className="animate-spin" /> : <Unlock size={18} />}
                                            Unlock
                                        </button>
                                    )}
                                </div>
                                {item.metadataLocked && (
                                    <p className="text-sm text-gray-400">
                                        Auto-refresh skips this item. Edits stay until you unlock.
                                    </p>
                                )}
                            </div>
                        )}

                        {mode === 'search' && (
                            <div className="space-y-4">
                                <div className="flex gap-3">
                                    <input type="text" value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search query (e.g. Blade Runner 2049)" className={inputCls} />
                                    <input type="number" value={year} onChange={(e) => setYear(e.target.value)} placeholder="Year" className={inputCls + ' w-28'} />
                                    <button type="button" onClick={() => searchMutation.mutate()} disabled={searchMutation.isPending || !query.trim()} className={btnPrimary}>
                                        {searchMutation.isPending ? <Loader2 size={18} className="animate-spin" /> : 'Search'}
                                    </button>
                                </div>
                                {searched && candidates.length === 0 && (
                                    <p className="text-base text-gray-500">No matches. Try a shorter query or include the year.</p>
                                )}
                                {candidates.length > 0 && (
                                    <ul className="space-y-2.5 max-h-[28rem] overflow-y-auto">
                                        {candidates.map((c) => (
                                            <li key={`${c.providerName}-${c.providerItemId}`} className="flex items-center gap-4 p-3 bg-black/30 rounded-lg">
                                                {c.posterUrl ? (
                                                    <img src={c.posterUrl} alt="" className="w-16 h-24 object-cover rounded shrink-0 bg-black/40" referrerPolicy="no-referrer" onError={(e) => { (e.target as HTMLImageElement).style.visibility = 'hidden'; }} />
                                                ) : (
                                                    <div className="w-16 h-24 bg-black/40 rounded shrink-0" />
                                                )}
                                                <div className="min-w-0 flex-1">
                                                    <div className="text-base text-white truncate">{c.title}{c.year ? ` (${c.year})` : ''}</div>
                                                    {c.subtitle && <div className="text-sm text-gray-500 truncate">{c.subtitle}</div>}
                                                    <div className="text-sm text-gray-600">{c.providerName}</div>
                                                </div>
                                                <button type="button" onClick={() => applyMutation.mutate(c)} disabled={applyMutation.isPending} className={btnPrimary}>
                                                    {applyMutation.isPending ? <Loader2 size={18} className="animate-spin" /> : 'Apply'}
                                                </button>
                                            </li>
                                        ))}
                                    </ul>
                                )}
                            </div>
                        )}

                        {mode === 'edit' && (
                            <div className="space-y-4">
                                <div>
                                    <label className="block text-sm text-gray-400 mb-1.5">Title</label>
                                    <input type="text" value={editTitle} onChange={(e) => setEditTitle(e.target.value)} className={inputCls} />
                                </div>
                                <div>
                                    <label className="block text-sm text-gray-400 mb-1.5">Overview</label>
                                    <textarea value={editOverview} onChange={(e) => setEditOverview(e.target.value)} rows={5} className={inputCls} />
                                </div>
                                <div className="flex gap-3">
                                    <div className="flex-1">
                                        <label className="block text-sm text-gray-400 mb-1.5">Year</label>
                                        <input type="number" value={editYear} onChange={(e) => setEditYear(e.target.value)} className={inputCls} />
                                    </div>
                                    <div className="flex-[2]">
                                        <label className="block text-sm text-gray-400 mb-1.5">Poster URL</label>
                                        <input type="url" value={editPoster} onChange={(e) => setEditPoster(e.target.value)} className={inputCls} />
                                    </div>
                                </div>
                                <button type="button" onClick={() => editMutation.mutate()} disabled={editMutation.isPending} className={btnPrimary}>
                                    {editMutation.isPending ? <Loader2 size={18} className="animate-spin" /> : 'Save (locks item)'}
                                </button>
                            </div>
                        )}
                    </div>
                </div>
            )}
        </>
    );
}
