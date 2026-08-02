import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { HdrTranscodePrompt } from './HdrTranscodePrompt';
import type { MediaVersion } from '../../types';

/**
 * QS-WI-005 — dialog branch tests for the pre-play HDR guardrail:
 * warn vs block button sets, the version offer, the software-load line's two wordings,
 * and the cause line consuming the SAME explain.reason.* keys as the explainer.
 */

vi.mock('react-i18next', () => ({
    // Natural-key t(): returns the key, with {{label}} interpolation for the version button.
    useTranslation: () => ({
        t: (key: string, params?: Record<string, unknown>) => {
            if (params?.defaultValue !== undefined && key.startsWith('explain.reason.unknown')) {
                return params.defaultValue as string;
            }
            if (params?.label) return `${key}:${params.label}`;
            return key;
        },
    }),
}));

const sdrVersion: MediaVersion = {
    id: 'sdr-1080',
    label: '1080p',
    height: 1080,
    size: 1,
    isPrimary: false,
    preferred: false,
    watched: false,
};

function makePlan(overrides: Partial<Parameters<typeof HdrTranscodePrompt>[0]['plan']> = {}) {
    return {
        toneMapIsSoftware: false,
        hardwareAccelerationEnabled: false,
        reasonCodes: [{ code: 'hdr.tonemap', params: {} }],
        ...overrides,
    };
}

function renderPrompt(props: Partial<Parameters<typeof HdrTranscodePrompt>[0]> = {}) {
    const handlers = {
        onPlayAnyway: vi.fn(),
        onPlayVersion: vi.fn(),
        onNeverShowAgain: vi.fn(),
        onCancel: vi.fn(),
    };
    render(
        <HdrTranscodePrompt
            plan={makePlan()}
            versionOffer={null}
            mode="warn"
            {...handlers}
            {...props}
        />,
    );
    return handlers;
}

describe('HdrTranscodePrompt', () => {
    it('is a labelled dialog (house modal semantics)', () => {
        renderPrompt();
        const dialog = screen.getByRole('dialog', { name: /hdrguard\.title/ });
        expect(dialog).toHaveAttribute('aria-modal', 'true');
        expect(dialog.getAttribute('aria-labelledby')).toBeTruthy();
    });

    it('warn mode shows Play anyway, Cancel, and Never show again — never pre-selected', () => {
        renderPrompt();
        expect(screen.getByText('hdrguard.quality')).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'hdrguard.playAnyway' })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'hdrguard.cancel' })).toBeInTheDocument();
        const neverShow = screen.getByRole('button', { name: 'hdrguard.neverShow' });
        // A plain button, not a pre-checked box: nothing is selected until the user acts.
        expect(neverShow).toBeInTheDocument();
        expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
    });

    it('offers the SDR version only when one exists in the group', () => {
        renderPrompt({ versionOffer: sdrVersion });
        expect(screen.getByRole('button', { name: 'hdrguard.playVersion:1080p' })).toBeInTheDocument();
    });

    it('omits the version button when there is no SDR sibling', () => {
        renderPrompt({ versionOffer: null });
        expect(screen.queryByRole('button', { name: /hdrguard\.playVersion/ })).not.toBeInTheDocument();
    });

    it('block mode offers only the version and cancel — no Play anyway, no Never show again', () => {
        renderPrompt({ mode: 'block', versionOffer: sdrVersion });
        expect(screen.getByText('hdrguard.blocked')).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'hdrguard.playVersion:1080p' })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'hdrguard.cancel' })).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'hdrguard.playAnyway' })).not.toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'hdrguard.neverShow' })).not.toBeInTheDocument();
    });

    it('block mode without an SDR sibling leaves only cancel', () => {
        renderPrompt({ mode: 'block', versionOffer: null });
        expect(screen.getByRole('button', { name: 'hdrguard.cancel' })).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /hdrguard\.play/ })).not.toBeInTheDocument();
    });

    it('software tone-map with no hw accel uses the very-CPU-intensive wording', () => {
        renderPrompt({ plan: makePlan({ toneMapIsSoftware: true, hardwareAccelerationEnabled: false }) });
        expect(screen.getByText('hdrguard.load.noHwAccel')).toBeInTheDocument();
        expect(screen.queryByText('hdrguard.load.partial')).not.toBeInTheDocument();
    });

    it('software tone-map despite configured hw accel uses the partly-on-CPU wording', () => {
        renderPrompt({ plan: makePlan({ toneMapIsSoftware: true, hardwareAccelerationEnabled: true }) });
        expect(screen.getByText('hdrguard.load.partial')).toBeInTheDocument();
        expect(screen.queryByText('hdrguard.load.noHwAccel')).not.toBeInTheDocument();
    });

    it('hardware tone-map omits the resource line entirely — quality is the only concern', () => {
        renderPrompt({ plan: makePlan({ toneMapIsSoftware: false, hardwareAccelerationEnabled: true }) });
        expect(screen.queryByText(/hdrguard\.load\./)).not.toBeInTheDocument();
    });

    it('cause lines consume the shared explain.reason.* strings (no parallel wording)', () => {
        renderPrompt({
            plan: makePlan({
                reasonCodes: [
                    { code: 'hdr.tonemap.subtitles', params: {} },
                    { code: 'subtitle.burn-in', params: {} },
                    { code: 'audio.codec.unsupported', params: { codec: 'dts' } }, // not an HDR cause → not shown
                ],
            }),
        });
        expect(screen.getByText('explain.reason.hdr.tonemap.subtitles')).toBeInTheDocument();
        expect(screen.getByText('explain.reason.subtitle.burn-in')).toBeInTheDocument();
        expect(screen.queryByText('explain.reason.audio.codec.unsupported')).not.toBeInTheDocument();
    });

    it('falls back to the first meaningful transcode cause when no HDR-specific code exists', () => {
        renderPrompt({
            plan: makePlan({
                reasonCodes: [
                    { code: 'directplay.supported', params: {} },
                    { code: 'bitrate.wan-cap', params: { kbps: '20000' } },
                ],
            }),
        });
        expect(screen.getByText('explain.reason.bitrate.wan-cap')).toBeInTheDocument();
    });

    it('wires every button to its handler', () => {
        const handlers = renderPrompt({ versionOffer: sdrVersion });
        fireEvent.click(screen.getByRole('button', { name: 'hdrguard.playAnyway' }));
        expect(handlers.onPlayAnyway).toHaveBeenCalledTimes(1);
        fireEvent.click(screen.getByRole('button', { name: 'hdrguard.playVersion:1080p' }));
        expect(handlers.onPlayVersion).toHaveBeenCalledWith(sdrVersion);
        fireEvent.click(screen.getByRole('button', { name: 'hdrguard.neverShow' }));
        expect(handlers.onNeverShowAgain).toHaveBeenCalledTimes(1);
        fireEvent.click(screen.getByRole('button', { name: 'hdrguard.cancel' }));
        expect(handlers.onCancel).toHaveBeenCalledTimes(1);
    });
});
