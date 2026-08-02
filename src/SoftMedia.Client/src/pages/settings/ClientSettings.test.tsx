import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import ClientSettings from './ClientSettings';
import { accountService } from '../../services/accountService';
import { useAuthStore } from '../../store/authStore';

vi.mock('../../services/accountService', () => ({
    accountService: {
        getStreamingLimits: vi.fn(),
    },
}));

const mocked = vi.mocked(accountService);

const PREFS_KEY = 'softmedia_preferences_guest';

function storedPrefs(): Record<string, string> {
    return JSON.parse(localStorage.getItem(PREFS_KEY) ?? '{}');
}

function renderPlayback() {
    const qc = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    return render(
        <QueryClientProvider client={qc}>
            <ClientSettings subsection="playback" />
        </QueryClientProvider>
    );
}

beforeEach(() => {
    localStorage.clear();
    useAuthStore.setState({ user: null });
    vi.clearAllMocks();
    mocked.getStreamingLimits.mockResolvedValue({
        lan: { maxBitrateKbps: 0, maxResolution: 0 },
        remote: { maxBitrateKbps: 20000, maxResolution: 1080 },
    });
});

describe('ClientSettings playback — QS-WI-008/009 copy', () => {
    it('frames the screen as "what this device asks for" and explains Auto honestly', () => {
        renderPlayback();

        expect(screen.getByText(/What this device asks for/)).toBeInTheDocument();
        // The trustworthy-Auto sentence (single-rendition reality, no bandwidth guessing).
        expect(screen.getByText(/the server picks direct play or remux when possible, else one transcode/))
            .toBeInTheDocument();
        expect(screen.getByText(/no client-side bandwidth guessing/)).toBeInTheDocument();
        // It points at the user-invoked explainer as the diagnostic.
        expect(screen.getByText(/Why is this playing this way\?/)).toBeInTheDocument();
    });

    it('renders the read-only "what the server allows you" line from /me/streaming-limits', async () => {
        renderPlayback();

        await waitFor(() =>
            expect(screen.getByText(/at home: unlimited bitrate, any resolution/)).toBeInTheDocument());
        expect(screen.getByText(/away: up to 20 Mbps, 1080p/)).toBeInTheDocument();
        expect(screen.getByText(/Set by your administrator/)).toBeInTheDocument();
        expect(mocked.getStreamingLimits).toHaveBeenCalledTimes(1);
    });

    it('drops the server-allows line when the endpoint is unreachable (no eternal "checking…")', async () => {
        mocked.getStreamingLimits.mockRejectedValue(new Error('network'));
        renderPlayback();

        await waitFor(() =>
            expect(screen.queryByText(/What the server allows you/)).not.toBeInTheDocument());
        // The rest of the screen (the asks) is unaffected.
        expect(screen.getByText(/What this device asks for/)).toBeInTheDocument();
    });
});

describe('ClientSettings — Media Tips (QS-WI-011)', () => {
    it('is ON by default and turning it off requires the confirm dialog FIRST', () => {
        renderPlayback();
        const toggle = screen.getByRole('switch', { name: /show media tips/i });
        expect(toggle).toHaveAttribute('aria-checked', 'true');

        fireEvent.click(toggle);

        // Nothing changed yet — the dialog is the gate.
        const dialog = screen.getByRole('dialog', { name: /turn off media tips/i });
        expect(toggle).toHaveAttribute('aria-checked', 'true');
        // Owner wording: complexity + diagnosing resource/quality issues + the
        // pointer at the user-invoked diagnostics.
        expect(within(dialog).getByText(/Streaming and transcoding are complex/)).toBeInTheDocument();
        expect(within(dialog).getByText(/resource usage and playback quality issues/)).toBeInTheDocument();
        expect(within(dialog).getByText(/Why is this playing this\s+way\?/)).toBeInTheDocument();
    });

    it('"Keep tips on" cancels: tips stay enabled', () => {
        renderPlayback();
        fireEvent.click(screen.getByRole('switch', { name: /show media tips/i }));

        fireEvent.click(screen.getByRole('button', { name: /keep tips on/i }));

        expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
        expect(screen.getByRole('switch', { name: /show media tips/i }))
            .toHaveAttribute('aria-checked', 'true');
        expect(storedPrefs().mediaTipsEnabled).not.toBe('false');
    });

    it('"Turn off" confirms: tips disabled and persisted', () => {
        renderPlayback();
        fireEvent.click(screen.getByRole('switch', { name: /show media tips/i }));

        fireEvent.click(screen.getByRole('button', { name: /^turn off$/i }));

        expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
        expect(screen.getByRole('switch', { name: /show media tips/i }))
            .toHaveAttribute('aria-checked', 'false');
        expect(storedPrefs().mediaTipsEnabled).toBe('false');
    });

    it('re-enabling needs no confirm and RESETS the per-prompt "Never show again" flags', () => {
        // A device where tips were off and the HDR prompt had been dismissed forever.
        localStorage.setItem(PREFS_KEY, JSON.stringify({
            mediaTipsEnabled: 'false',
            showHdrTranscodeWarning: 'false',
        }));
        renderPlayback();
        const toggle = screen.getByRole('switch', { name: /show media tips/i });
        expect(toggle).toHaveAttribute('aria-checked', 'false');

        fireEvent.click(toggle);

        expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
        expect(toggle).toHaveAttribute('aria-checked', 'true');
        expect(storedPrefs().mediaTipsEnabled).toBe('true');
        // The QS-WI-005 guardrail flag is restored — the prompt can show again.
        expect(storedPrefs().showHdrTranscodeWarning).toBe('true');
    });
});
