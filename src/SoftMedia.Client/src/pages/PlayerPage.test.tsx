import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { AxiosError, type AxiosResponse } from 'axios';
import PlayerPage from './PlayerPage';
import api from '../services/api';

vi.mock('../services/api', () => ({
    default: { get: vi.fn(), post: vi.fn() },
    API_URL: '/api/v1',
}));
vi.mock('../components/player/VideoPlayer', () => ({
    default: () => <div data-testid="video-player" />,
}));

const mockedApi = vi.mocked(api, true);

function axios404() {
    return new AxiosError('Not Found', 'ERR_BAD_REQUEST', undefined, undefined, { status: 404 } as AxiosResponse);
}

function renderPlayer() {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={qc}>
            <MemoryRouter initialEntries={['/play/item-1']}>
                <Routes>
                    <Route path="/play/:id" element={<PlayerPage />} />
                    <Route path="/" element={<div>home-page</div>} />
                </Routes>
            </MemoryRouter>
        </QueryClientProvider>
    );
}

beforeEach(() => {
    vi.clearAllMocks();
});

/** SR-WI-052 — replace the bare "Error loading media" with 404 differentiation + Retry. */
describe('PlayerPage error states', () => {
    it('a 404 says the item is gone and offers Home, not a useless Retry', async () => {
        mockedApi.get.mockRejectedValue(axios404());
        renderPlayer();

        expect(await screen.findByText(/could not be found/i)).toBeInTheDocument();
        expect(screen.getByRole('link', { name: /go home/i })).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /retry/i })).not.toBeInTheDocument();
    });

    it('other failures offer Retry, and a successful retry plays the video', async () => {
        mockedApi.get
            .mockRejectedValueOnce(new AxiosError('Network Error', 'ERR_NETWORK'))
            .mockResolvedValue({ data: { id: 'item-1', title: 'Movie', type: 'Movie' } });
        renderPlayer();

        expect(await screen.findByText(/couldn't load this video/i)).toBeInTheDocument();
        fireEvent.click(screen.getByRole('button', { name: /retry/i }));

        expect(await screen.findByTestId('video-player')).toBeInTheDocument();
        expect(mockedApi.get).toHaveBeenCalledTimes(2);
    });
});
