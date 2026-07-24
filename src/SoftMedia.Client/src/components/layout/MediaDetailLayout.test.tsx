import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { ComponentProps } from 'react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import MediaDetailLayout, { formatResumeTime } from './MediaDetailLayout';
import { MediaType, type MediaItem } from '../../types';
import { toast } from 'sonner';

vi.mock('../../services/api', () => ({
    default: { get: vi.fn().mockResolvedValue({ data: [] }), post: vi.fn() },
}));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));
vi.mock('../../hooks/useMediaTokenRefresh', () => ({
    useMediaTokenRefresh: vi.fn(),
    default: vi.fn(),
}));
vi.mock('../../lib/mediaImageUrl', () => ({
    resolveHeroPosterUrl: (p: string | undefined) => p ?? null,
    resolveBackdropUrl: (p: string | undefined) => p ?? null,
}));
vi.mock('../details/ExtraPlayerModal', () => ({ ExtraPlayerModal: () => null }));

const movie = {
    id: 'm1',
    title: 'Test Movie',
    type: MediaType.Movie,
    isFavorite: false,
    watched: false,
} as unknown as MediaItem;

function renderLayout(props: Partial<ComponentProps<typeof MediaDetailLayout>> = {}) {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            <MemoryRouter>
                <MediaDetailLayout item={movie} onPlay={() => {}} {...props}>
                    <div />
                </MediaDetailLayout>
            </MemoryRouter>
        </QueryClientProvider>,
    );
}

beforeEach(() => vi.clearAllMocks());

describe('formatResumeTime', () => {
    it('formats sub-hour positions as M:SS', () => {
        expect(formatResumeTime(0)).toBe('0:00');
        expect(formatResumeTime(65)).toBe('1:05');
        expect(formatResumeTime(599.9)).toBe('9:59');
    });

    it('formats hour-plus positions as H:MM:SS', () => {
        expect(formatResumeTime(3600)).toBe('1:00:00');
        expect(formatResumeTime(4025)).toBe('1:07:05');
    });
});

// SR-WI-053 — with a resume position the single Play becomes a split control:
// primary "Resume from …", secondary "Play from beginning". Without one the
// layout must stay exactly a single Play button.
describe('resume / start-over split control', () => {
    it('renders a single Play button when no resume position exists', () => {
        renderLayout();
        expect(screen.getByRole('button', { name: /^Play$/ })).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /Play from beginning/ })).toBeNull();
        expect(screen.queryByText(/Resume from/)).toBeNull();
    });

    it('renders Resume + Play-from-beginning when a resume position exists', () => {
        const onPlay = vi.fn();
        const onPlayFromBeginning = vi.fn();
        renderLayout({ onPlay, onPlayFromBeginning, resumePositionSeconds: 4025 });

        const resume = screen.getByRole('button', { name: 'Resume from 1:07:05' });
        const restart = screen.getByRole('button', { name: 'Play from beginning' });

        fireEvent.click(resume);
        expect(onPlay).toHaveBeenCalledTimes(1);

        fireEvent.click(restart);
        expect(onPlayFromBeginning).toHaveBeenCalledTimes(1);
    });

    it('stays a single Play when a position exists but no start-over handler is wired', () => {
        renderLayout({ resumePositionSeconds: 120 });
        expect(screen.getByRole('button', { name: /^Play$/ })).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /Play from beginning/ })).toBeNull();
    });
});

// SR-WI-050 (CLI-L) — Play must not be a silent no-op while a prerequisite
// (e.g. album tracks) is still loading: disabled + spinner instead.
describe('playPending', () => {
    it('disables the Play button while pending', () => {
        const onPlay = vi.fn();
        renderLayout({ onPlay, playPending: true });

        const play = screen.getByRole('button', { name: /^Play$/ });
        expect(play).toBeDisabled();
        fireEvent.click(play);
        expect(onPlay).not.toHaveBeenCalled();
    });
});

// SR-WI-050 — the Share button was a dead control (no onClick). It now copies
// the canonical detail URL, mirroring the photo variant's behavior.
describe('share button', () => {
    it('copies the canonical detail URL and shows a success toast', async () => {
        const writeText = vi.fn().mockResolvedValue(undefined);
        Object.defineProperty(navigator, 'clipboard', {
            value: { writeText },
            configurable: true,
        });

        renderLayout();
        fireEvent.click(screen.getByRole('button', { name: 'Copy link to Test Movie' }));

        expect(writeText).toHaveBeenCalledWith(`${window.location.origin}/media/m1`);
        await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Link copied to clipboard'));
    });

    it('shows an error toast when the clipboard write fails', async () => {
        const writeText = vi.fn().mockRejectedValue(new Error('denied'));
        Object.defineProperty(navigator, 'clipboard', {
            value: { writeText },
            configurable: true,
        });

        renderLayout();
        fireEvent.click(screen.getByRole('button', { name: 'Copy link to Test Movie' }));

        await waitFor(() => expect(toast.error).toHaveBeenCalledWith('Could not copy the link'));
    });
});

// SR-WI-050 — the sidebar icon buttons had title only; assistive tech needs
// accessible names (the photo variant already did this right).
describe('sidebar action a11y', () => {
    it('exposes accessible names for favorite and watched toggles', () => {
        renderLayout();
        expect(screen.getByRole('button', { name: 'Add to favorites' })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Mark as watched' })).toBeInTheDocument();
    });

    it('flips the names with state', () => {
        renderLayout({
            item: { ...movie, isFavorite: true, watched: true } as unknown as MediaItem,
        });
        expect(screen.getByRole('button', { name: 'Remove from favorites' })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Mark as unwatched' })).toBeInTheDocument();
    });
});
