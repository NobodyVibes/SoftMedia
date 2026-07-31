import React, { useState } from 'react';
import { toast } from 'sonner';
import { extractApiError } from '../../services/apiError';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Cast, Save, RefreshCw, AlertTriangle } from 'lucide-react';
import { settingsService, type AppSetting } from '../../services/settingsService';
import { libraryService } from '../../services/libraryService';

const MOVIE_RATINGS = ['G', 'PG', 'PG-13', 'R', 'NC-17'];
const TV_RATINGS = ['TV-Y', 'TV-Y7', 'TV-G', 'TV-PG', 'TV-14', 'TV-MA'];

/**
 * R-WI-010 — admin UI for the DLNA / UPnP media server (shipped in P4-004 but previously
 * unconfigurable from the app). Surfaces the four DLNA settings that were server-only:
 * EnableDlna, DlnaServerName, DlnaExposedLibraries (as a library checklist, not raw GUIDs), and
 * DlnaMaxContentRatings (as per-type dropdowns serialized to the {"Movie":..,"TV":..} JSON the
 * server expects, avoiding a raw-JSON footgun). Self-contained (own fetch + save); the shared
 * page "Save Changes" re-syncs from the same ['settings'] query after this card invalidates it.
 */
export const DlnaSettingsCard: React.FC = () => {
    const queryClient = useQueryClient();
    const { data: settings } = useQuery({ queryKey: ['settings'], queryFn: settingsService.getAll });
    const { data: libraries = [] } = useQuery({ queryKey: ['libraries'], queryFn: libraryService.getAll });

    const [enabled, setEnabled] = useState(false);
    const [serverName, setServerName] = useState('SoftMedia');
    const [exposed, setExposed] = useState<Set<string>>(new Set());
    const [movieRating, setMovieRating] = useState('');
    const [tvRating, setTvRating] = useState('');

    // Initialise the form ONCE from the first settings load. A later refetch (e.g. this card's own
    // save invalidating ['settings'], or a window refocus) must NOT clobber the admin's in-progress
    // edits, so we don't re-sync on every settings change. Seeded during render
    // (react.dev: "adjusting state when props change") so the defaults never
    // flash before the stored values arrive.
    const [initialized, setInitialized] = useState(false);
    if (settings && !initialized) {
        setInitialized(true);
        const get = (k: string) => settings.find(s => s.key === k)?.value ?? '';
        setEnabled(get('EnableDlna') === 'true');
        setServerName(get('DlnaServerName') || 'SoftMedia');
        setExposed(new Set(get('DlnaExposedLibraries').split(',').map(s => s.trim()).filter(Boolean)));
        try {
            const parsed = JSON.parse(get('DlnaMaxContentRatings') || '{}');
            setMovieRating(typeof parsed?.Movie === 'string' ? parsed.Movie : '');
            setTvRating(typeof parsed?.TV === 'string' ? parsed.TV : '');
        } catch {
            setMovieRating('');
            setTvRating('');
        }
    }

    // DLNA only serves audio/video items, so only these library types are eligible to expose.
    const eligibleLibraries = libraries.filter(l => l.type === 'Movie' || l.type === 'TV' || l.type === 'Music');

    const saveMutation = useMutation({
        mutationFn: (updated: AppSetting[]) => settingsService.update(updated),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['settings'] });
            toast.success('DLNA settings saved. Enabling or disabling DLNA takes effect after a server restart.');
        },
        onError: (error: unknown) => {
            toast.error(extractApiError(error, 'Failed to save DLNA settings'));
        },
    });

    // Build the payload from the CURRENT form state at click time (not a mutationFn closure).
    const handleSave = () => {
        const ratings: Record<string, string> = {};
        if (movieRating) ratings.Movie = movieRating;
        if (tvRating) ratings.TV = tvRating;

        const updated: AppSetting[] = (settings ?? [])
            .filter(s => s.group === 'DLNA')
            .map(s => {
                let value = s.value;
                if (s.key === 'EnableDlna') value = enabled ? 'true' : 'false';
                else if (s.key === 'DlnaServerName') value = serverName.trim() || 'SoftMedia';
                else if (s.key === 'DlnaExposedLibraries') value = Array.from(exposed).join(',');
                else if (s.key === 'DlnaMaxContentRatings') value = Object.keys(ratings).length ? JSON.stringify(ratings) : '';
                return { ...s, value };
            });
        saveMutation.mutate(updated);
    };

    const toggleLibrary = (id: string) => setExposed(prev => {
        const next = new Set(prev);
        if (next.has(id)) next.delete(id); else next.add(id);
        return next;
    });

    return (
        <div className="bg-white/5 rounded-xl p-6 border border-white/10">
            <div className="flex items-center gap-3 mb-4">
                <Cast className="h-5 w-5 text-blue-400" />
                <h3 className="text-lg font-semibold text-white">DLNA / UPnP Media Server</h3>
            </div>

            <div className="space-y-6">
                {/* Enable + restart note */}
                <div className="flex flex-col gap-2">
                    <div className="flex items-center gap-3">
                        <button
                            type="button"
                            role="switch"
                            aria-checked={enabled}
                            aria-label="Enable DLNA"
                            onClick={() => setEnabled(v => !v)}
                            className={`w-12 h-6 rounded-full transition-colors relative flex-shrink-0 ${enabled ? 'bg-[#007AFF]' : 'bg-white/10'}`}
                        >
                            <div className={`absolute top-1 w-4 h-4 rounded-full bg-white transition-all ${enabled ? 'left-7' : 'left-1'}`} />
                        </button>
                        <label className="text-sm font-medium text-gray-300">Enable DLNA server</label>
                    </div>
                    <p className="text-xs text-gray-500">
                        Enabling or disabling DLNA takes effect after a server restart.
                    </p>
                </div>

                {/* Security caveat */}
                <div className="flex items-start gap-2 rounded-lg bg-amber-500/10 border border-amber-500/30 p-3">
                    <AlertTriangle className="h-4 w-4 text-amber-400 flex-shrink-0 mt-0.5" />
                    <p className="text-xs text-amber-200/90">
                        DLNA has no login and no per-user access control. Any device on your LAN can browse and
                        play the exposed libraries (it is never reachable from the internet). Only expose libraries
                        suitable for everyone on your network, and set a content-rating ceiling below.
                    </p>
                </div>

                {/* Server name */}
                <div className="flex flex-col gap-2">
                    <label htmlFor="dlna-name" className="text-sm font-medium text-gray-300">Server name (shown on TVs)</label>
                    <input
                        id="dlna-name"
                        type="text"
                        value={serverName}
                        onChange={(e) => setServerName(e.target.value)}
                        className="max-w-md bg-gray-700 border border-gray-600 rounded px-3 py-2 text-white focus:outline-none focus:border-[#007AFF]"
                    />
                </div>

                {/* Exposed libraries */}
                <div className="flex flex-col gap-2">
                    <label className="text-sm font-medium text-gray-300">Exposed libraries</label>
                    {eligibleLibraries.length === 0 ? (
                        <p className="text-xs text-gray-500">No audio/video libraries to expose.</p>
                    ) : (
                        <div className="flex flex-col gap-2">
                            {eligibleLibraries.map(lib => (
                                <label key={lib.id} className="flex items-center gap-2 text-sm text-gray-300 cursor-pointer">
                                    <input
                                        type="checkbox"
                                        checked={exposed.has(lib.id)}
                                        onChange={() => toggleLibrary(lib.id)}
                                        className="accent-[#007AFF]"
                                    />
                                    {lib.name} <span className="text-xs text-gray-500">({lib.type})</span>
                                </label>
                            ))}
                        </div>
                    )}
                    {exposed.size === 0 && (
                        <p className="text-xs text-gray-500">Nothing selected — DLNA exposes no libraries (default).</p>
                    )}
                </div>

                {/* Content-rating ceiling */}
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                    <div className="flex flex-col gap-2">
                        <label htmlFor="dlna-movie" className="text-sm font-medium text-gray-300">Max movie rating (DLNA)</label>
                        <select
                            id="dlna-movie"
                            value={movieRating}
                            onChange={(e) => setMovieRating(e.target.value)}
                            className="bg-gray-700 border border-gray-600 rounded px-3 py-2 text-white focus:outline-none focus:border-[#007AFF]"
                        >
                            <option value="">No limit</option>
                            {MOVIE_RATINGS.map(r => <option key={r} value={r}>{r}</option>)}
                        </select>
                    </div>
                    <div className="flex flex-col gap-2">
                        <label htmlFor="dlna-tv" className="text-sm font-medium text-gray-300">Max TV rating (DLNA)</label>
                        <select
                            id="dlna-tv"
                            value={tvRating}
                            onChange={(e) => setTvRating(e.target.value)}
                            className="bg-gray-700 border border-gray-600 rounded px-3 py-2 text-white focus:outline-none focus:border-[#007AFF]"
                        >
                            <option value="">No limit</option>
                            {TV_RATINGS.map(r => <option key={r} value={r}>{r}</option>)}
                        </select>
                    </div>
                </div>

                <div className="pt-2">
                    <button
                        type="button"
                        onClick={handleSave}
                        disabled={saveMutation.isPending}
                        className="flex items-center gap-2 px-4 py-2 bg-[#007AFF] hover:bg-[#005BB5] text-white rounded-lg font-medium transition-colors disabled:opacity-50"
                    >
                        {saveMutation.isPending ? <RefreshCw className="animate-spin" size={16} /> : <Save size={16} />}
                        Save DLNA Settings
                    </button>
                </div>
            </div>
        </div>
    );
};
