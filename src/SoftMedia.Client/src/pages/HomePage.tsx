import { useCallback, useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useLibraries, useLibraryRecent, useHeroItems } from '../hooks/useLibrary';
import api from '../services/api';
import HeroSection from '../components/ui/HeroSection';
import MediaRow from '../components/ui/MediaRow';
import ScopeToggle from '../components/ui/ScopeToggle';
import { userPreferencesService } from '../services/userPreferencesService';
import { watchlistService } from '../services/watchlistService';
import { continueWatchingService } from '../services/continueWatchingService';
import { type Library, type MediaItem, MediaType } from '../types';

/**
 * SR-WI-052: rows report their query-error state up to the page. Every home row
 * self-suppresses on error exactly as it does on empty (a server hiccup must not
 * leave broken skeletons mid-page), but the PAGE shows one shared banner with a
 * Retry, instead of the old behavior of silently rendering a blank home page.
 */
type RowErrorReporter = (rowKey: string, isError: boolean) => void;

function useReportRowError(rowKey: string, isError: boolean, onErrorStateChange: RowErrorReporter) {
    useEffect(() => {
        onErrorStateChange(rowKey, isError);
        // A row that unmounts (e.g. a library disappears) must not leave a stale
        // failure pinned on the page banner.
        return () => onErrorStateChange(rowKey, false);
    }, [rowKey, isError, onErrorStateChange]);
}

/**
 * Continue Watching — the user's in-progress Movies + TV shows, newest-first. Rendered FIRST in
 * the user-state cluster (directly below the hero). Self-suppresses when empty. TV shows appear as
 * a single show card; MediaCard's play handler resolves a Series to its resume episode, so clicking
 * Play continues from wherever the user left off — never lists individual episodes.
 */
function ContinueWatchingRow({ onErrorStateChange }: { onErrorStateChange: RowErrorReporter }) {
    const { data: items, isLoading, isError } = useQuery<MediaItem[]>({
        queryKey: ['continueWatching'],
        queryFn: () => continueWatchingService.list(20),
    });
    useReportRowError('continueWatching', isError, onErrorStateChange);

    if (isLoading || isError) return null;
    if (!items || items.length === 0) return null;

    return (
        <MediaRow
            title="Continue Watching"
            items={items}
            // The grid uses the SQL-expressible half of this row's rule (started, not
            // finished). This row additionally applies the credits/95% completion check
            // in code — it compares each item's CreditsStart against its own Duration
            // and cannot be a query predicate — so the grid may still list something
            // sitting right at the end credits that the row has already dropped.
            viewAllLink="/browse?inProgress=true&types=Movie,Series&sortBy=lastplayed"
        />
    );
}

/** Server preference key backing the Most Watched row's scope toggle. */
const MOST_WATCHED_SCOPE_KEY = 'home.mostWatched.scope';
type MostWatchedScope = 'everyone' | 'me';

const SCOPE_OPTIONS = [
    { value: 'everyone' as const, label: 'Everyone' },
    { value: 'me' as const, label: 'Me' },
] as const;

/** Criteria a row can hand to /browse; null when the row isn't URL-reproducible. */
interface HomeRowFilter {
    genre?: string | null;
    decade?: number | null;
    unplayed?: boolean | null;
    libraryId?: string | null;
    sortBy?: string | null;
    inProgress?: boolean | null;
    /** Media types the row is narrowed to, e.g. ["Movie","Series"]. */
    types?: string[] | null;
}

interface HomeRow {
    kind: string;
    title: string;
    items: MediaItem[];
    filter?: HomeRowFilter | null;
}

/**
 * Build the /browse link for a row. Returns undefined when the row carries no filter,
 * so the "See more" link is simply omitted rather than pointing somewhere that would
 * show a different set of items than the row did.
 */
function browseLinkFor(filter: HomeRowFilter | null | undefined): string | undefined {
    if (!filter) return undefined;

    const params = new URLSearchParams();
    if (filter.genre) params.set('genre', filter.genre);
    if (filter.decade != null) params.set('decade', String(filter.decade));
    if (filter.unplayed) params.set('unplayed', 'true');
    if (filter.libraryId) params.set('libraryId', filter.libraryId);
    if (filter.sortBy) params.set('sortBy', filter.sortBy);
    if (filter.inProgress) params.set('inProgress', 'true');
    // Must ride along, or a narrowed row (the video-only genre spotlight) would open a
    // grid containing the very albums and books it excluded.
    if (filter.types?.length) params.set('types', filter.types.join(','));

    const query = params.toString();
    return query ? `/browse?${query}` : undefined;
}

/**
 * R-WI-020 — personalized rows ("Most Watched", "Top picks for you",
 * "More <genre>") derived from play history. Self-suppresses for users with no
 * history (the server returns an empty list).
 *
 * The Most Watched row carries an Everyone/Me toggle. Scope is a server-side
 * preference rather than local state so it follows the user across devices, and
 * it is written with a single-key PUT — UserPreferencesService upserts per key,
 * so this never disturbs the user's other preferences.
 */
function PersonalizedRows({ onErrorStateChange }: { onErrorStateChange: RowErrorReporter }) {
    const queryClient = useQueryClient();

    const { data: preferences } = useQuery({
        queryKey: ['userPreferences'],
        queryFn: () => userPreferencesService.getPreferences(),
        staleTime: 5 * 60 * 1000,
    });

    // Anything other than an explicit "me" means everyone — matches the server's
    // own fallback, so a malformed stored value can't desync the toggle from the
    // rows it labels.
    const scope: MostWatchedScope = preferences?.[MOST_WATCHED_SCOPE_KEY] === 'me' ? 'me' : 'everyone';

    const { mutate: setScope, isPending: scopeChanging } = useMutation({
        mutationFn: (next: MostWatchedScope) =>
            userPreferencesService.updatePreferences({ [MOST_WATCHED_SCOPE_KEY]: next }),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['userPreferences'] });
        },
    });

    const { data: rows, isLoading, isError } = useQuery<HomeRow[]>({
        // Scope is part of the key so each variant caches separately and toggling
        // back is instant rather than a refetch.
        queryKey: ['homeRows', scope],
        queryFn: async () => (await api.get('/media/home-rows', { params: { scope } })).data,
        staleTime: 5 * 60 * 1000, // taste shifts slowly; don't refetch on every focus
    });
    // Only the rows query feeds the banner — a preferences failure silently degrades
    // to the 'everyone' default and must not flag the whole page as broken.
    useReportRowError('homeRows', isError, onErrorStateChange);

    if (isLoading || isError || !rows || rows.length === 0) return null;

    return (
        <>
            {rows.map((row) => (
                <MediaRow
                    key={row.kind === 'most-watched' ? row.kind : `${row.kind}:${row.title}`}
                    title={row.title}
                    items={row.items}
                    viewAllLink={browseLinkFor(row.filter)}
                    headerAction={row.kind === 'most-watched' ? (
                        <ScopeToggle
                            label="Most watched scope"
                            value={scope}
                            options={SCOPE_OPTIONS}
                            onChange={setScope}
                            disabled={scopeChanging}
                        />
                    ) : undefined}
                />
            ))}
        </>
    );
}

/**
 * Wave E3 — the user's Watchlist row. Rendered between the hero and the
 * Recently Added rows so it sits in the cluster of "user-state" rows
 * (Watchlist, Continue Watching, …). The component self-suppresses when
 * the watchlist is empty so users without anything saved don't see an
 * empty row.
 */
function WatchlistRow({ onErrorStateChange }: { onErrorStateChange: RowErrorReporter }) {
    const { data: items, isLoading, isError } = useQuery<MediaItem[]>({
        queryKey: ['watchlist'],
        queryFn: () => watchlistService.list(50),
    });
    useReportRowError('watchlist', isError, onErrorStateChange);

    if (isLoading || isError) return null;
    if (!items || items.length === 0) return null;

    return (
        <MediaRow
            title="Your Watchlist"
            items={items}
        />
    );
}

/**
 * Component to handle fetching and rendering a "Recently Added" row for a specific library.
 */
function LibraryRecentRow({
    library,
    onErrorStateChange
}: {
    library: Library;
    onErrorStateChange: RowErrorReporter;
}) {
    const { data: recentItems, isLoading, isError } = useLibraryRecent(library.id);
    useReportRowError(`libraryRecent:${library.id}`, isError, onErrorStateChange);

    if (isLoading || isError || !recentItems || recentItems.length === 0) return null;

    return (
        <MediaRow
            key={library.id}
            title={`Recently Added ${library.name}`}
            items={recentItems || []}
            // Carry the "recently added" criterion through instead of dumping the user
            // on the unfiltered library sorted by title, which is what the old
            // /libraries/{id} link did — the destination shared no ordering with the
            // row it was attached to.
            viewAllLink={`/browse?libraryId=${library.id}&sortBy=dateadded`}
            libraryType={library.type}
        />
    );
}

// Types that get a Recently Added row. A FILTER, not an ordering — row order follows
// the admin-configured library order the API returns (see sortedLibraries below).
const SUPPORTED_TYPES: Library['type'][] = ['Movie', 'TV', 'Music', 'Book', 'Game'];

export default function HomePage() {
    const { data: libraries, isError: librariesError } = useLibraries();
    const { data: heroItems, isLoading: heroLoading, isError: heroError } = useHeroItems();
    const navigate = useNavigate();
    const queryClient = useQueryClient();

    // SR-WI-052: which rows currently sit in a query-error state. One page-level
    // banner (below) covers them all — no per-row error spam, and rows that
    // succeeded keep rendering normally.
    const [failedRows, setFailedRows] = useState<Record<string, true>>({});

    const reportRowError = useCallback<RowErrorReporter>((rowKey, isError) => {
        setFailedRows(prev => {
            if (Boolean(prev[rowKey]) === isError) return prev;
            const nextState = { ...prev };
            if (isError) nextState[rowKey] = true;
            else delete nextState[rowKey];
            return nextState;
        });
    }, []);

    const hasErrors = heroError || librariesError || Object.keys(failedRows).length > 0;

    // Refetch everything that failed (hero, libraries, and any row) in one go.
    const retryFailed = useCallback(() => {
        void queryClient.refetchQueries({ predicate: (query) => query.state.status === 'error' });
    }, [queryClient]);

    // Determine which libraries to show (excluding Photos and unknown types).
    //
    // Deliberately NOT re-sorted. The API returns libraries in the admin-configured
    // order (Library.Order — the same reorderable value the sidebar renders verbatim),
    // and the Recently Added rows must march down the page in that same sequence. This
    // used to sort by a hardcoded type ranking first, which put Music above Books on
    // the home page while the sidebar said the opposite.
    const sortedLibraries = useMemo(() => {
        return libraries?.filter(l => l.type !== 'Photo' && SUPPORTED_TYPES.includes(l.type));
    }, [libraries]);

    const handlePlay = async (item: MediaItem) => {
        if (!item) return;

        if (item.type === MediaType.Series) { // Use MediaType enum
            try {
                const response = await api.get(`/series/${item.id}/next-episode`);
                const nextEpisode = response.data;
                navigate(`/play/${nextEpisode.episodeId}`);
            } catch (error) {
                console.error('[HomePage] Failed to fetch next episode for hero item:', error);
                navigate(`/media/${item.id}`);
            }
        } else if (item.type === MediaType.Movie || item.type === MediaType.Episode) {
            navigate(`/play/${item.id}`);
        } else {
            // The hero rotation includes Albums/Books/Games — none are playable by
            // the VIDEO player, and /play/{albumId} used to land users in a broken
            // player against /stream/{albumId} → 404. Their detail pages own the
            // right play/read affordances.
            navigate(`/media/${item.id}`);
        }
    };

    const handleMoreInfo = (item: MediaItem) => {
        if (!item) return;
        navigate(`/media/${item.id}`);
    };

    return (
        <div className="pb-20">
            {/* Hero Section */}
            <HeroSection
                items={heroItems || []}
                isLoading={heroLoading}
                onPlay={handlePlay}
                onMoreInfo={handleMoreInfo}
            />

            {/* SR-WI-052: single page-level banner when one or more sections failed to
                load. Rows that errored self-suppress (no broken skeletons); the rest of
                the page renders normally around this. */}
            {hasErrors && (
                <div
                    role="alert"
                    className="mx-6 mb-6 flex items-center justify-between gap-4 p-3 rounded-lg bg-red-500/10 border border-red-500/20 text-red-400 text-sm"
                >
                    <span>Some parts of your home page couldn't be loaded.</span>
                    <button
                        type="button"
                        onClick={retryFailed}
                        className="shrink-0 px-3 py-1.5 rounded-md border border-red-500/40 font-medium hover:bg-red-500/20 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                    >
                        Retry
                    </button>
                </div>
            )}

            {/* User-state rows: Continue Watching first (directly below the hero), then Watchlist. */}
            <div className="flex flex-col gap-8">
                <ContinueWatchingRow onErrorStateChange={reportRowError} />

                {/* R-WI-020 — taste-based rows; render nothing without history */}
                <PersonalizedRows onErrorStateChange={reportRowError} />
                <WatchlistRow onErrorStateChange={reportRowError} />
            </div>

            {/* Dynamic Recently Added Rows per Library */}
            <div className="flex flex-col gap-8 mt-8">
                {sortedLibraries?.map(library => (
                    <LibraryRecentRow
                        key={library.id}
                        library={library}
                        onErrorStateChange={reportRowError}
                    />
                ))}
            </div>
        </div>
    );
}
