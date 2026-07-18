import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { HistoryPrivacyCard } from './HistoryPrivacyCard';
import { accountService } from '../../services/accountService';

vi.mock('../../services/accountService', () => ({
    accountService: {
        getHistoryPreferences: vi.fn(),
        setHistoryPreferences: vi.fn(),
        clearHistory: vi.fn(),
    },
}));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

const mocked = vi.mocked(accountService);

function renderCard() {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
    return render(<QueryClientProvider client={qc}><HistoryPrivacyCard /></QueryClientProvider>);
}

beforeEach(() => {
    vi.clearAllMocks();
    mocked.getHistoryPreferences.mockResolvedValue({ recordPlaybackHistory: true });
    mocked.setHistoryPreferences.mockResolvedValue();
    mocked.clearHistory.mockResolvedValue({ deleted: 3 });
});

/** R-WI-013 privacy — user-owned toggle + clear, with an explicit confirm step on clear. */
describe('HistoryPrivacyCard', () => {
    it('shows the recording state from the server and toggles it off', async () => {
        renderCard();
        const toggle = await screen.findByRole('switch', { name: /record my history/i });
        await waitFor(() => expect(toggle).toHaveAttribute('aria-checked', 'true'));

        fireEvent.click(toggle);

        await waitFor(() => expect(mocked.setHistoryPreferences).toHaveBeenCalledWith(false));
    });

    it('clear requires an explicit confirmation before calling the API', async () => {
        renderCard();
        fireEvent.click(await screen.findByRole('button', { name: /clear my history/i }));

        expect(mocked.clearHistory).not.toHaveBeenCalled(); // not yet — confirm first
        fireEvent.click(screen.getByRole('button', { name: /yes, erase it/i }));

        await waitFor(() => expect(mocked.clearHistory).toHaveBeenCalledTimes(1));
    });

    it('cancel backs out without clearing', async () => {
        renderCard();
        fireEvent.click(await screen.findByRole('button', { name: /clear my history/i }));
        fireEvent.click(screen.getByRole('button', { name: /cancel/i }));

        expect(mocked.clearHistory).not.toHaveBeenCalled();
        expect(screen.getByRole('button', { name: /clear my history/i })).toBeInTheDocument();
    });
});
