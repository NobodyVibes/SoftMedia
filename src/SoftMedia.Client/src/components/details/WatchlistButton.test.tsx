import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { WatchlistButton } from './WatchlistButton';
import { watchlistService } from '../../services/watchlistService';
import { toast } from 'sonner';

vi.mock('../../services/watchlistService', () => ({
    watchlistService: { toggle: vi.fn() },
}));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

const mockedToggle = vi.mocked(watchlistService.toggle);

function renderButton(props: Partial<Parameters<typeof WatchlistButton>[0]> = {}) {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');
    render(
        <QueryClientProvider client={queryClient}>
            <WatchlistButton mediaId="m1" isWatchlisted={false} title="Test Movie" {...props} />
        </QueryClientProvider>,
    );
    return { invalidateSpy };
}

beforeEach(() => vi.clearAllMocks());

// SR-WI-050 — a successful toggle used to invalidate ['media-detail', id], a key
// NOTHING reads (the detail page renders from ['media', id]). item.isWatchlisted
// therefore stayed stale and the prop sync-back visually REVERTED the toggle.
// These tests pin the correct keys so the bug cannot silently return.
describe('WatchlistButton cache invalidation', () => {
    it('invalidates the detail query key the page actually uses', async () => {
        mockedToggle.mockResolvedValue(undefined);
        const { invalidateSpy } = renderButton();

        fireEvent.click(screen.getByRole('button', { name: 'Add to watchlist' }));

        await waitFor(() => expect(mockedToggle).toHaveBeenCalledWith('m1', true));
        await waitFor(() =>
            expect(invalidateSpy).toHaveBeenCalledWith(
                expect.objectContaining({ queryKey: ['media', 'm1'] }),
            ),
        );
    });

    it('invalidates the watchlist list (home row + watchlist page) with refetchType all', async () => {
        mockedToggle.mockResolvedValue(undefined);
        const { invalidateSpy } = renderButton();

        fireEvent.click(screen.getByRole('button', { name: 'Add to watchlist' }));

        await waitFor(() =>
            expect(invalidateSpy).toHaveBeenCalledWith(
                expect.objectContaining({ queryKey: ['watchlist'], refetchType: 'all' }),
            ),
        );
    });

    it('never invalidates the dead media-detail key', async () => {
        mockedToggle.mockResolvedValue(undefined);
        const { invalidateSpy } = renderButton();

        fireEvent.click(screen.getByRole('button', { name: 'Add to watchlist' }));

        await waitFor(() => expect(invalidateSpy).toHaveBeenCalled());
        for (const call of invalidateSpy.mock.calls) {
            expect((call[0] as { queryKey?: unknown[] } | undefined)?.queryKey?.[0]).not.toBe('media-detail');
        }
    });
});

describe('WatchlistButton optimistic state', () => {
    it('flips immediately while in flight and shows a success toast on resolve', async () => {
        // Controlled promise: the optimistic flip must be observable BEFORE the
        // request settles (after settle, the sync-back re-syncs to the
        // isWatchlisted prop, which only updates via the parent's requery).
        let resolveToggle!: () => void;
        mockedToggle.mockImplementation(
            () => new Promise<void>((resolve) => { resolveToggle = resolve; }),
        );
        renderButton();

        fireEvent.click(screen.getByRole('button', { name: 'Add to watchlist' }));

        // onMutate resolves in a microtask — waitFor while the request is
        // still pending observes the optimistic flip.
        await waitFor(() =>
            expect(screen.getByRole('button', { name: 'In watchlist' })).toHaveAttribute('aria-pressed', 'true'),
        );

        resolveToggle();
        await waitFor(() =>
            expect(toast.success).toHaveBeenCalledWith('Added to watchlist: Test Movie'),
        );
    });

    it('reverts the optimistic flip and toasts on error', async () => {
        mockedToggle.mockRejectedValue(new Error('nope'));
        renderButton();

        fireEvent.click(screen.getByRole('button', { name: 'Add to watchlist' }));

        await waitFor(() => expect(toast.error).toHaveBeenCalledWith('nope'));
        expect(screen.getByRole('button', { name: 'Add to watchlist' })).toHaveAttribute('aria-pressed', 'false');
    });
});
