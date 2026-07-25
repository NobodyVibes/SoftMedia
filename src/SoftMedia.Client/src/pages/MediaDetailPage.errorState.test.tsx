import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import MediaDetailPage from './MediaDetailPage';
import api from '../services/api';

vi.mock('../services/api', () => ({
    default: { get: vi.fn(), post: vi.fn() },
}));
vi.mock('../hooks/useMediaHub', () => ({ useMediaHub: vi.fn() }));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

const mockedGet = vi.mocked(api.get);

/** Axios-shaped rejection so isAxiosError() recognizes it. */
function axiosError(status: number) {
    return Object.assign(new Error(`Request failed with status code ${status}`), {
        isAxiosError: true,
        response: { status },
    });
}

function renderPage() {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            <MemoryRouter initialEntries={['/media/m1']}>
                <Routes>
                    <Route path="/media/:id" element={<MediaDetailPage />} />
                </Routes>
            </MemoryRouter>
        </QueryClientProvider>,
    );
}

beforeEach(() => vi.clearAllMocks());

// SR-WI-050/052 (detail-page slice) — the error state was a bare "Error loading
// media" string: no retry, no 404 distinction. Pin the split behavior.
describe('MediaDetailPage error state', () => {
    it('distinguishes 404 as a gone item with a Go home action (no futile retry)', async () => {
        mockedGet.mockRejectedValue(axiosError(404));
        renderPage();

        expect(await screen.findByText('This item no longer exists')).toBeInTheDocument();
        // "Go home", not history-back: the previous history entry may be the
        // player for this same now-deleted item (back-buttons plan: hierarchical
        // navigation everywhere, never navigate(-1)).
        expect(screen.getByRole('button', { name: 'Go home' })).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
    });

    it('shows a generic failure with a Retry action for non-404 errors', async () => {
        mockedGet.mockRejectedValue(axiosError(500));
        renderPage();

        expect(await screen.findByText('Could not load this item')).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Go home' })).toBeNull();
    });

    it('Retry refetches the detail query', async () => {
        mockedGet.mockRejectedValue(axiosError(500));
        renderPage();

        await screen.findByRole('button', { name: 'Retry' });
        expect(mockedGet).toHaveBeenCalledTimes(1);

        fireEvent.click(screen.getByRole('button', { name: 'Retry' }));

        await waitFor(() => expect(mockedGet).toHaveBeenCalledTimes(2));
        expect(mockedGet).toHaveBeenCalledWith('/media/m1');
    });
});
