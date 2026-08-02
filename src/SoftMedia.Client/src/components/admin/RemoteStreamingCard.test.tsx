import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RemoteStreamingCard } from './RemoteStreamingCard';
import { settingsService, type AppSetting } from '../../services/settingsService';

vi.mock('../../services/settingsService', () => ({
    settingsService: {
        getAll: vi.fn(),
        update: vi.fn(),
    },
}));

vi.mock('sonner', () => ({
    toast: { success: vi.fn(), error: vi.fn() },
}));

const mocked = vi.mocked(settingsService);

const settings: AppSetting[] = [
    { key: 'MaxStreamingBitrate', value: '20000', group: 'Streaming', description: '' },
    { key: 'MaxStreamingBitrateLan', value: '0', group: 'Streaming', description: '' },
    { key: 'RemoteMaxResolution', value: 'original', group: 'Streaming', description: '' },
    // An unrelated setting the card must never touch on save.
    { key: 'DefaultStreamingQuality', value: 'auto', group: 'Streaming', description: '' },
];

function renderCard() {
    const qc = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    return render(<QueryClientProvider client={qc}><RemoteStreamingCard /></QueryClientProvider>);
}

beforeEach(() => {
    vi.clearAllMocks();
    mocked.getAll.mockResolvedValue(settings);
    mocked.update.mockResolvedValue();
});

describe('RemoteStreamingCard (QS-WI-001)', () => {
    it('renders the shipped caps and the CGNAT/VPN caveat', async () => {
        renderCard();

        // The Save button is disabled until the form is seeded from the settings query —
        // waiting on it (not on a value that equals the pre-seed default) avoids racing
        // the seed, which would clobber later edits.
        await waitFor(() =>
            expect(screen.getByRole('button', { name: /save remote streaming/i })).toBeEnabled());
        expect(screen.getByLabelText(/Remote bitrate limit/i)).toHaveValue(20000);
        expect(screen.getByLabelText(/Home \(LAN\) bitrate limit/i)).toHaveValue(0);
        expect(screen.getByLabelText(/Remote resolution limit/i)).toHaveValue('original');
        // The Tailscale/CGNAT-counts-as-home caveat is part of the card's contract.
        expect(screen.getByText(/Tailscale/)).toBeInTheDocument();
        // And so is the pointer to per-user overrides.
        expect(screen.getByText(/Per-user streaming limits/)).toBeInTheDocument();
    });

    it('saves ONLY its three keys, with the edited values', async () => {
        renderCard();
        await waitFor(() =>
            expect(screen.getByRole('button', { name: /save remote streaming/i })).toBeEnabled());

        fireEvent.change(screen.getByLabelText(/Remote bitrate limit/i), { target: { value: '8000' } });
        fireEvent.change(screen.getByLabelText(/Remote resolution limit/i), { target: { value: '1080p' } });
        fireEvent.click(screen.getByRole('button', { name: /save remote streaming/i }));

        await waitFor(() => expect(mocked.update).toHaveBeenCalledTimes(1));
        const payload = mocked.update.mock.calls[0][0] as AppSetting[];
        expect(payload.map(s => s.key).sort()).toEqual(
            ['MaxStreamingBitrate', 'MaxStreamingBitrateLan', 'RemoteMaxResolution']);
        expect(payload.find(s => s.key === 'MaxStreamingBitrate')!.value).toBe('8000');
        expect(payload.find(s => s.key === 'MaxStreamingBitrateLan')!.value).toBe('0');
        expect(payload.find(s => s.key === 'RemoteMaxResolution')!.value).toBe('1080p');
    });
});
