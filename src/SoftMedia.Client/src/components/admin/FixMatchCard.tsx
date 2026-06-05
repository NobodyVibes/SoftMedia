import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Lock, Unlock, Wand2, X, Loader2, Edit3 } from 'lucide-react';
import { toast } from 'sonner';
import { adminService, type MetadataSearchCandidate } from '../../services/adminService';
import { type MediaItem } from '../../types';

/**
 * Admin-only "Fix Match" card (P3-WI-003). Rendered above the detail view so the
 * admin can correct a wrong auto-match without renaming files. Three modes:
 *   - Re-search a metadata provider, pick a candidate, apply (auto-locks).
 *   - Manually edit fields (title/overview/year/poster), saves with auto-lock.
 *   - Unlock to re-enable auto-refresh.
 */
export function FixMatchCard({ item }: { item: MediaItem }) {
    const queryClient = useQueryClient();
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

    const searchMutation = useMutation({
        mutationFn: () => adminService.searchMatch(item.id, query.trim(), year.trim() ? Number(year) : null),
        onSuccess: (res) => { setCandidates(res); setSearched(true); },
        onError: () => toast.error('Search failed'),
    });

    const applyMutation = useMutation({
        mutationFn: (c: MetadataSearchCandidate) => adminService.applyMatch(item.id, c.providerName, c.providerItemId),
        onSuccess: () => {
            toast.success('Match applied — item locked');
            setMode('idle');
            setCandidates([]);
            setSearched(false);
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
            setMode('idle');
            invalidate();
        },
        onError: () => toast.error('Save failed'),
    });

    const unlockMutation = useMutation({
        mutationFn: () => adminService.unlockMatch(item.id),
        onSuccess: () => { toast.success('Unlocked'); invalidate(); },
        onError: () => toast.error('Unlock failed'),
    });

    const openEdit = () => {
        setEditTitle(item.title ?? '');
        setEditOverview((item as unknown as { overview?: string }).overview ?? '');
        setEditYear(item.year != null ? String(item.year) : '');
        setEditPoster(item.posterPath ?? '');
        setMode('edit');
    };

    const inputCls = 'w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-primary';
    const btnPrimary = 'inline-flex items-center gap-2 px-3 py-1.5 text-sm bg-primary hover:bg-primary/90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 text-white rounded-lg disabled:opacity-50';
    const btnGhost = 'inline-flex items-center gap-2 px-3 py-1.5 text-sm bg-white/5 hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 text-white rounded-lg';

    return (
        <div className="bg-white/5 rounded-xl p-4 border border-white/10 mb-4">
            <div className="flex items-center justify-between mb-3">
                <div className="flex items-center gap-2">
                    {item.metadataLocked ? <Lock className="w-4 h-4 text-amber-400" /> : <Wand2 className="w-4 h-4 text-primary" />}
                    <h3 className="text-sm font-semibold text-white">
                        {item.metadataLocked ? 'Metadata locked' : 'Fix match'}
                    </h3>
                    {item.metadataLocked && item.metadataLockedAt && (
                        <span className="text-xs text-gray-500">since {new Date(item.metadataLockedAt).toLocaleDateString()}</span>
                    )}
                </div>
                <div className="flex items-center gap-2">
                    {mode === 'idle' && (
                        <>
                            <button type="button" onClick={() => { setMode('search'); setQuery(item.title ?? ''); setSearched(false); }} className={btnGhost}>
                                <Wand2 size={14} /> Re-search
                            </button>
                            <button type="button" onClick={openEdit} className={btnGhost}>
                                <Edit3 size={14} /> Edit fields
                            </button>
                            {item.metadataLocked && (
                                <button type="button" onClick={() => unlockMutation.mutate()} disabled={unlockMutation.isPending}
                                    className="inline-flex items-center gap-2 px-3 py-1.5 text-sm bg-amber-500/20 hover:bg-amber-500/30 text-amber-300 rounded-lg disabled:opacity-50">
                                    {unlockMutation.isPending ? <Loader2 size={14} className="animate-spin" /> : <Unlock size={14} />}
                                    Unlock
                                </button>
                            )}
                        </>
                    )}
                    {mode !== 'idle' && (
                        <button type="button" onClick={() => setMode('idle')} className="p-1.5 rounded hover:bg-white/10 text-gray-400" aria-label="Close">
                            <X size={16} />
                        </button>
                    )}
                </div>
            </div>

            {item.metadataLocked && mode === 'idle' && (
                <p className="text-xs text-gray-400">
                    Auto-refresh skips this item. Edits stay until you unlock.
                </p>
            )}

            {mode === 'search' && (
                <div className="space-y-3">
                    <div className="flex gap-2">
                        <input type="text" value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search query (e.g. Blade Runner 2049)" className={inputCls} />
                        <input type="number" value={year} onChange={(e) => setYear(e.target.value)} placeholder="Year" className={inputCls + ' w-24'} />
                        <button type="button" onClick={() => searchMutation.mutate()} disabled={searchMutation.isPending || !query.trim()} className={btnPrimary}>
                            {searchMutation.isPending ? <Loader2 size={14} className="animate-spin" /> : 'Search'}
                        </button>
                    </div>
                    {searched && candidates.length === 0 && (
                        <p className="text-sm text-gray-500">No matches. Try a shorter query or include the year.</p>
                    )}
                    {candidates.length > 0 && (
                        <ul className="space-y-2 max-h-96 overflow-y-auto">
                            {candidates.map((c) => (
                                <li key={`${c.providerName}-${c.providerItemId}`} className="flex items-center gap-3 p-2 bg-black/30 rounded-lg">
                                    {c.posterUrl ? (
                                        // eslint-disable-next-line @next/next/no-img-element
                                        <img src={c.posterUrl} alt="" className="w-12 h-16 object-cover rounded shrink-0 bg-black/40" referrerPolicy="no-referrer" onError={(e) => { (e.target as HTMLImageElement).style.visibility = 'hidden'; }} />
                                    ) : (
                                        <div className="w-12 h-16 bg-black/40 rounded shrink-0" />
                                    )}
                                    <div className="min-w-0 flex-1">
                                        <div className="text-sm text-white truncate">{c.title}{c.year ? ` (${c.year})` : ''}</div>
                                        {c.subtitle && <div className="text-xs text-gray-500 truncate">{c.subtitle}</div>}
                                        <div className="text-xs text-gray-600">{c.providerName}</div>
                                    </div>
                                    <button type="button" onClick={() => applyMutation.mutate(c)} disabled={applyMutation.isPending} className={btnPrimary}>
                                        {applyMutation.isPending ? <Loader2 size={14} className="animate-spin" /> : 'Apply'}
                                    </button>
                                </li>
                            ))}
                        </ul>
                    )}
                </div>
            )}

            {mode === 'edit' && (
                <div className="space-y-3">
                    <div>
                        <label className="block text-xs text-gray-400 mb-1">Title</label>
                        <input type="text" value={editTitle} onChange={(e) => setEditTitle(e.target.value)} className={inputCls} />
                    </div>
                    <div>
                        <label className="block text-xs text-gray-400 mb-1">Overview</label>
                        <textarea value={editOverview} onChange={(e) => setEditOverview(e.target.value)} rows={3} className={inputCls} />
                    </div>
                    <div className="flex gap-2">
                        <div className="flex-1">
                            <label className="block text-xs text-gray-400 mb-1">Year</label>
                            <input type="number" value={editYear} onChange={(e) => setEditYear(e.target.value)} className={inputCls} />
                        </div>
                        <div className="flex-[2]">
                            <label className="block text-xs text-gray-400 mb-1">Poster URL</label>
                            <input type="url" value={editPoster} onChange={(e) => setEditPoster(e.target.value)} className={inputCls} />
                        </div>
                    </div>
                    <button type="button" onClick={() => editMutation.mutate()} disabled={editMutation.isPending} className={btnPrimary}>
                        {editMutation.isPending ? <Loader2 size={14} className="animate-spin" /> : 'Save (locks item)'}
                    </button>
                </div>
            )}
        </div>
    );
}
