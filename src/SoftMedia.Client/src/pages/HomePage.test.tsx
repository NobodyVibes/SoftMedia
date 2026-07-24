import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import HomePage from './HomePage';
import { continueWatchingService } from '../services/continueWatchingService';
import { watchlistService } from '../services/watchlistService';
import { userPreferencesService } from '../services/userPreferencesService';
import { libraryService } from '../services/libraryService';
import api from '../services/api';
import type { MediaItem } from '../types';

vi.mock('../services/continueWatchingService', () => ({
    continueWatchingService: { list: vi.fn() },
}));
vi.mock('../services/watchlistService', () => ({
    watchlistService: { list: vi.fn() },
}));
vi.mock('../services/userPreferencesService', () => ({
    userPreferencesService: { getPreferences: vi.fn(), updatePreferences: vi.fn() },
}));
vi.mock('../services/libraryService', () => ({
    libraryService: {
        getAll: vi.fn(),
        getHeroItems: vi.fn(),
        getRecentlyAddedForLibrary: vi.fn(),
    },
}));
vi.mock('../services/api', () => ({
    default: { get: vi.fn(), post: vi.fn() },
    API_URL: '/api/v1',
}));
// Presentational children are not under test — keep them cheap and observable.
vi.mock('../components/ui/HeroSection', () => ({
    default: () => <div data-testid="hero" />,
}));
vi.mock('../components/ui/MediaRow', () => ({
    default: ({ title }: { title: string }) => <div data-testid="media-row">{title}</div>,
}));
vi.mock('../components/ui/ScopeToggle', () => ({
    default: () => null,
}));

const mockedContinueWatching = vi.mocked(continueWatchingService);
const mockedWatchlist = vi.mocked(watchlistService);
const mockedPreferences = vi.mocked(userPreferencesService);
const mockedLibraries = vi.mocked(libraryService);
const mockedApi = vi.mocked(api, true);

const item = (id: string, title: string) => ({ id, title, type: 'Movie' }) as unknown as MediaItem;

function renderHome() {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
    return render(
        <QueryClientProvider client={qc}>
            <MemoryRouter>
                <HomePage />
            </MemoryRouter>
        </QueryClientProvider>
    );
}

beforeEach(() => {
    vi.clearAllMocks();
    mockedContinueWatching.list.mockResolvedValue([item('cw1', 'In Progress Movie')]);
    mockedWatchlist.list.mockResolvedValue([item('wl1', 'Saved Movie')]);
    mockedPreferences.getPreferences.mockResolvedValue({});
    mockedLibraries.getAll.mockResolvedValue([]);
    mockedLibraries.getHeroItems.mockResolvedValue([]);
    mockedLibraries.getRecentlyAddedForLibrary.mockResolvedValue([]);
    mockedApi.get.mockResolvedValue({ data: [] }); // /media/home-rows
});

/** SR-WI-052 — a failed row shows ONE page-level banner; healthy rows keep rendering. */
describe('HomePage error banner', () => {
    it('shows a single banner with Retry when one row errors, while successful rows still render', async () => {
        mockedContinueWatching.list.mockRejectedValue(new Error('server hiccup'));

        renderHome();

        // The healthy watchlist row renders despite the sibling failure.
        expect(await screen.findByText('Your Watchlist')).toBeInTheDocument();

        // Exactly one page-level banner, with a Retry affordance.
        const banner = await screen.findByRole('alert');
        expect(banner).toHaveTextContent(/couldn't be loaded/i);
        expect(screen.getAllByRole('alert')).toHaveLength(1);
        expect(screen.getByRole('button', { name: /retry/i })).toBeInTheDocument();

        // The failed row self-suppresses instead of rendering a broken skeleton.
        expect(screen.queryByText('Continue Watching')).not.toBeInTheDocument();
    });

    it('Retry refetches the failed query and clears the banner on success', async () => {
        mockedContinueWatching.list
            .mockRejectedValueOnce(new Error('server hiccup'))
            .mockResolvedValue([item('cw1', 'In Progress Movie')]);

        renderHome();

        const retry = await screen.findByRole('button', { name: /retry/i });
        fireEvent.click(retry);

        // The row recovers and the banner goes away.
        expect(await screen.findByText('Continue Watching')).toBeInTheDocument();
        await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
        expect(mockedContinueWatching.list).toHaveBeenCalledTimes(2);
    });

    it('renders no banner when everything loads', async () => {
        renderHome();

        expect(await screen.findByText('Your Watchlist')).toBeInTheDocument();
        expect(await screen.findByText('Continue Watching')).toBeInTheDocument();
        expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    });
});
