import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ActiveSessionsCard } from './ActiveSessionsCard';
import { adminService, type ActiveSession } from '../../services/adminService';

vi.mock('../../services/adminService', () => ({
    adminService: {
        getActiveSessions: vi.fn(),
        terminateSession: vi.fn(),
    },
}));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() } }));
vi.mock('react-i18next', () => ({
    // Natural-key t() with the interpolation the real i18n config performs —
    // the aria-label under test embeds {{name}}.
    useTranslation: () => ({
        t: (key: string, vars?: Record<string, unknown>) =>
            key.replace(/\{\{(\w+)\}\}/g, (_, v: string) => String(vars?.[v] ?? '')),
    }),
}));

const mocked = vi.mocked(adminService);

const transcodeRow: ActiveSession = {
    type: 'Transcode',
    state: 'Transcoding',
    userId: 'u1',
    userName: 'alice',
    mediaId: 'm1',
    mediaTitle: 'Big Movie',
    positionSeconds: 180,
    durationSeconds: 5400,
    startedAt: new Date().toISOString(),
    resolution: '720p',
    codec: 'h264',
    maxBitrateKbps: 3000,
    canTerminate: true,
    subtitleTrackIndex: null,
    streamId: 'sid-1',
};

const directPlayRow: ActiveSession = {
    type: 'DirectPlay',
    state: 'Playing',
    userId: 'u2',
    userName: 'bob',
    mediaId: 'm2',
    mediaTitle: 'A Song',
    positionSeconds: 65,
    durationSeconds: 329,
    startedAt: new Date().toISOString(),
    resolution: null,
    codec: null,
    maxBitrateKbps: null,
    canTerminate: false,
    subtitleTrackIndex: null,
    streamId: null,
};

function renderCard() {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
    return render(<QueryClientProvider client={qc}><ActiveSessionsCard /></QueryClientProvider>);
}

beforeEach(() => {
    vi.clearAllMocks();
    mocked.getActiveSessions.mockResolvedValue([transcodeRow, directPlayRow]);
    mocked.terminateSession.mockResolvedValue();
});

/** R-WI-016 — Now-Playing card: both session kinds listed; Stop is transcode-only
 *  and confirm-gated (killing someone's stream must not be a single misclick). */
describe('ActiveSessionsCard', () => {
    it('lists transcode and direct-play rows with quality and progress', async () => {
        renderCard();

        expect(await screen.findByText('alice')).toBeInTheDocument();
        expect(screen.getByText('Big Movie')).toBeInTheDocument();
        expect(screen.getByText('720p · h264 · 3000 kbps')).toBeInTheDocument();
        expect(screen.getByText('3:00 / 1:30:00')).toBeInTheDocument();

        expect(screen.getByText('bob')).toBeInTheDocument();
        expect(screen.getByText('Direct Play')).toBeInTheDocument();
        expect(screen.getByText('Original')).toBeInTheDocument();
    });

    it('offers Stop only for the transcode row, gated behind a confirm step', async () => {
        renderCard();

        // Exactly one Stop button (the direct-play row is read-only).
        const stop = await screen.findByRole('button', { name: /stop the stream for alice/i });
        expect(screen.getAllByRole('button', { name: /stop the stream for/i })).toHaveLength(1);

        fireEvent.click(stop);
        expect(mocked.terminateSession).not.toHaveBeenCalled(); // not yet — confirm first

        fireEvent.click(screen.getByRole('button', { name: /yes, stop it/i }));
        await waitFor(() => expect(mocked.terminateSession).toHaveBeenCalledWith(transcodeRow));
    });

    it('confirm can be cancelled without terminating', async () => {
        renderCard();

        fireEvent.click(await screen.findByRole('button', { name: /stop the stream for alice/i }));
        fireEvent.click(screen.getByRole('button', { name: /cancel/i }));

        expect(mocked.terminateSession).not.toHaveBeenCalled();
        expect(screen.getByRole('button', { name: /stop the stream for alice/i })).toBeInTheDocument();
    });

    it('shows the empty state when nothing is playing', async () => {
        mocked.getActiveSessions.mockResolvedValue([]);
        renderCard();

        expect(await screen.findByText('Nothing is playing right now.')).toBeInTheDocument();
    });

    it('a failed fetch shows an error line, NOT the "nothing is playing" empty state', async () => {
        mocked.getActiveSessions.mockRejectedValue(new Error('down'));
        renderCard();

        expect(await screen.findByText(/could not load sessions/i)).toBeInTheDocument();
        expect(screen.queryByText('Nothing is playing right now.')).toBeNull();
    });

    it('terminate hitting a 404 reports "already ended" rather than a failure', async () => {
        const err = Object.assign(new Error('gone'), {
            isAxiosError: true,
            response: { status: 404 },
        });
        mocked.terminateSession.mockRejectedValue(err);
        const { toast } = await import('sonner');
        renderCard();

        fireEvent.click(await screen.findByRole('button', { name: /stop the stream for alice/i }));
        fireEvent.click(screen.getByRole('button', { name: /yes, stop it/i }));

        await waitFor(() => expect(toast.info).toHaveBeenCalled());
        expect(toast.error).not.toHaveBeenCalled();
    });
});
