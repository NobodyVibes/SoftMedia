import { useState, useMemo } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Search, Plus, Loader2, Check, X as XIcon } from 'lucide-react';
import { toast } from 'sonner';
import { searchService } from '../../services/searchService';
import { playlistService } from '../../services/playlistService';
import { useDebounce } from '../../hooks/useDebounce';
import { resolveArtworkUrl } from '../../lib/mediaImageUrl';
import { useMediaTokenRefresh } from '../../hooks/useMediaTokenRefresh';
import { formatDuration, cn } from '../../lib/utils';
import { MediaType, type MediaItem } from '../../types';

interface AddTracksPanelProps {
    playlistId: string;
    /** Track ids already in the playlist — drives the "already added" hint. */
    existingMediaItemIds: string[];
    onClose: () => void;
}

/** Per-library cap on the search; the flattened, audio-only list is capped again below. */
const SEARCH_LIMIT = 25;
const MAX_RESULTS = 40;

/**
 * Search-and-add panel for a playlist's own page.
 *
 * Before this existed, the only way to put a track in a playlist was to find it
 * somewhere else in the app and use its row's "add to playlist" button — so the
 * empty state of a brand-new playlist could do nothing but describe a button on
 * another page. Adding from here never navigates away and never closes the
 * panel, so queueing up several tracks is one continuous action.
 *
 * Results are audio only. The global search returns every media type, but the
 * server rejects non-audio playlist items by design (v1 scope), so offering a
 * movie here would only produce a 400.
 */
export function AddTracksPanel({ playlistId, existingMediaItemIds, onClose }: AddTracksPanelProps) {
    useMediaTokenRefresh();
    const queryClient = useQueryClient();
    const [query, setQuery] = useState('');
    const debouncedQuery = useDebounce(query, 300);
    // Ids added during this session of the panel. The playlist query does refetch,
    // but this is what lets a row switch to "Added" the instant the call returns
    // rather than after the round trip.
    const [justAdded, setJustAdded] = useState<string[]>([]);

    const trimmed = debouncedQuery.trim();

    const { data: groups, isFetching } = useQuery({
        queryKey: ['playlistTrackSearch', trimmed],
        queryFn: () => searchService.globalSearch(trimmed, SEARCH_LIMIT),
        enabled: trimmed.length >= 2,
    });

    const tracks: MediaItem[] = useMemo(() => {
        if (!groups) return [];
        return groups
            .flatMap(g => g.items)
            .filter(item => item.type === MediaType.Audio)
            .slice(0, MAX_RESULTS);
    }, [groups]);

    const addMutation = useMutation({
        mutationFn: (track: MediaItem) => playlistService.addItems(playlistId, [track.id]),
        onSuccess: (_, track) => {
            setJustAdded(prev => [...prev, track.id]);
            queryClient.invalidateQueries({ queryKey: ['playlist', playlistId] });
            queryClient.invalidateQueries({ queryKey: ['playlists'] });
            toast.success(`Added "${track.title}"`);
        },
        onError: (e: unknown) => toast.error(e instanceof Error ? e.message : 'Could not add track'),
    });

    const getImageUrl = resolveArtworkUrl; // shared; /cache/images is token-gated (AA-WI-001)

    const alreadyIn = (id: string) => existingMediaItemIds.includes(id) || justAdded.includes(id);

    return (
        <div className="bg-white/5 border border-white/10 rounded-xl overflow-hidden mb-6">
            <div className="flex items-center gap-2 px-4 py-3 border-b border-white/5 bg-white/[0.03]">
                <Search className="w-4 h-4 text-gray-500 shrink-0" />
                <input
                    autoFocus
                    type="search"
                    value={query}
                    onChange={(e) => setQuery(e.target.value)}
                    placeholder="Search your music for tracks to add…"
                    aria-label="Search for tracks to add"
                    className="flex-1 bg-transparent text-sm text-white placeholder-gray-500 focus:outline-none min-h-[36px]"
                />
                {isFetching && <Loader2 className="w-4 h-4 animate-spin text-gray-500 shrink-0" />}
                <button
                    type="button"
                    onClick={onClose}
                    aria-label="Close add tracks"
                    className="text-gray-500 hover:text-white focus-visible:text-white focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded p-2 min-w-[44px] min-h-[44px] flex items-center justify-center"
                >
                    <XIcon className="w-4 h-4" />
                </button>
            </div>

            <div className="max-h-96 overflow-y-auto">
                {trimmed.length < 2 ? (
                    <p className="px-4 py-6 text-sm text-gray-500 text-center">
                        Type at least two characters to search by track, artist, or album.
                    </p>
                ) : tracks.length === 0 && !isFetching ? (
                    <p className="px-4 py-6 text-sm text-gray-500 text-center">
                        No tracks match “{trimmed}”.
                    </p>
                ) : (
                    <ul className="divide-y divide-white/5">
                        {tracks.map(track => {
                            const added = alreadyIn(track.id);
                            const pending = addMutation.isPending && addMutation.variables?.id === track.id;
                            return (
                                <li key={track.id} className="flex items-center gap-3 px-4 py-2.5 hover:bg-white/5 transition-colors">
                                    <img
                                        src={getImageUrl(track.posterPath)}
                                        referrerPolicy="no-referrer"
                                        loading="lazy"
                                        decoding="async"
                                        width={40}
                                        height={40}
                                        alt=""
                                        className="w-10 h-10 rounded object-cover bg-gray-800 shrink-0"
                                    />
                                    <div className="flex-1 min-w-0">
                                        <div className="text-sm text-white truncate">{track.title}</div>
                                        <div className="text-xs text-gray-500 truncate">
                                            {(track.metadata?.artist as string) || 'Unknown artist'}
                                            {track.durationSeconds ? ` · ${formatDuration(track.durationSeconds)}` : ''}
                                        </div>
                                    </div>
                                    {/* Enabled even when the track is already present: duplicates
                                        are allowed by design (a deliberate repeat in a set), so
                                        this reports state rather than blocking the action. */}
                                    <button
                                        type="button"
                                        onClick={() => addMutation.mutate(track)}
                                        disabled={pending}
                                        aria-label={added ? `Add ${track.title} again` : `Add ${track.title}`}
                                        className={cn(
                                            'shrink-0 inline-flex items-center gap-1.5 px-3 py-2 rounded-lg text-xs font-medium transition-colors min-h-[44px] disabled:opacity-50',
                                            'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400',
                                            added
                                                ? 'text-gray-400 hover:bg-white/10'
                                                : 'text-primary hover:bg-primary/10'
                                        )}
                                    >
                                        {pending ? (
                                            <Loader2 className="w-3.5 h-3.5 animate-spin" />
                                        ) : added ? (
                                            <Check className="w-3.5 h-3.5" />
                                        ) : (
                                            <Plus className="w-3.5 h-3.5" />
                                        )}
                                        {added ? 'Added' : 'Add'}
                                    </button>
                                </li>
                            );
                        })}
                    </ul>
                )}
            </div>
        </div>
    );
}

export default AddTracksPanel;
