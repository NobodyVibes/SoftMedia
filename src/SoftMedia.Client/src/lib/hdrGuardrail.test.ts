import { describe, it, expect, beforeEach } from 'vitest';
import { markAutoAdvance, beginPlaybackSitting, acknowledgeSitting, pickSdrVersionOffer, shouldShowHdrPrompt } from './hdrGuardrail';
import type { MediaVersion } from '../types';

/**
 * QS-WI-005 — the sitting hand-off is the mechanism behind "auto-advance within one
 * sitting prompts once": auto-advance NAVIGATES (the player remounts), so the state
 * crosses the remount via sessionStorage. These tests pin the behavioral contract from
 * the plan's §7 definition: consecutive automatic transitions keep the sitting; any
 * manual play starts a new one.
 */

function makeVersion(overrides: Partial<MediaVersion> & { id: string }): MediaVersion {
    return {
        label: '1080p',
        size: 1,
        isPrimary: false,
        preferred: false,
        watched: false,
        ...overrides,
    };
}

beforeEach(() => {
    sessionStorage.clear();
});

describe('HDR guardrail sitting hand-off', () => {
    it('a manual play (no hand-off) is never covered', () => {
        expect(beginPlaybackSitting()).toBe(false);
    });

    it('an answered prompt covers the following auto-advance', () => {
        // Mount 1: prompt shown, user answers "Play anyway".
        beginPlaybackSitting();
        acknowledgeSitting();
        // Auto-advance to the next episode…
        markAutoAdvance();
        // Mount 2 (the next episode): still covered — no re-prompt.
        expect(beginPlaybackSitting()).toBe(true);
    });

    it('the answer keeps covering an unbroken binge', () => {
        beginPlaybackSitting();
        acknowledgeSitting();
        for (let episode = 0; episode < 3; episode++) {
            markAutoAdvance();
            expect(beginPlaybackSitting()).toBe(true);
        }
    });

    it('a manual play after the binge clears the old answer', () => {
        beginPlaybackSitting();
        acknowledgeSitting();
        markAutoAdvance();
        expect(beginPlaybackSitting()).toBe(true);
        // …later, a MANUAL play (detail page / episode row / version switch): no hand-off.
        expect(beginPlaybackSitting()).toBe(false);
        // And the cleared answer doesn't resurrect on a later auto-advance.
        markAutoAdvance();
        expect(beginPlaybackSitting()).toBe(false);
    });

    it('auto-advance without any prior answer still prompts (first HDR mid-binge)', () => {
        // SDR episodes auto-advanced without a prompt; the first HDR episode must prompt.
        beginPlaybackSitting();
        markAutoAdvance();
        expect(beginPlaybackSitting()).toBe(false);
    });

    it('each hand-off is consumed: a stale flag never leaks to a later manual play', () => {
        markAutoAdvance();
        beginPlaybackSitting();          // consumed here
        expect(beginPlaybackSitting()).toBe(false); // manual play: nothing left over
    });
});

describe('shouldShowHdrPrompt (QS-WI-011 suppression contract)', () => {
    /** A qualifying warn-mode fresh load with everything enabled — the prompting baseline. */
    const promptable = {
        policy: 'warn' as string | null,
        toneMapPlanned: true,
        freshLoad: true,
        mediaTipsEnabled: true,
        neverShowAgain: false,
        sittingCovered: false,
    };

    it('warn prompts on a qualifying fresh load', () => {
        expect(shouldShowHdrPrompt(promptable)).toBe(true);
    });

    it('Media Tips OFF suppresses the warn prompt (unsolicited surface)', () => {
        expect(shouldShowHdrPrompt({ ...promptable, mediaTipsEnabled: false })).toBe(false);
    });

    it('Media Tips OFF never suppresses the block dialog — admin rule wins', () => {
        expect(shouldShowHdrPrompt({
            ...promptable, policy: 'block', mediaTipsEnabled: false, neverShowAgain: true, sittingCovered: true,
        })).toBe(true);
    });

    it('"Never show again" suppresses warn but not block', () => {
        expect(shouldShowHdrPrompt({ ...promptable, neverShowAgain: true })).toBe(false);
        expect(shouldShowHdrPrompt({ ...promptable, policy: 'block', neverShowAgain: true })).toBe(true);
    });

    it('an answered auto-advance sitting suppresses warn re-prompts', () => {
        expect(shouldShowHdrPrompt({ ...promptable, sittingCovered: true })).toBe(false);
    });

    it('never fires without a planned tone-map, a policy, or a fresh load', () => {
        expect(shouldShowHdrPrompt({ ...promptable, toneMapPlanned: false })).toBe(false);
        expect(shouldShowHdrPrompt({ ...promptable, policy: null })).toBe(false);
        expect(shouldShowHdrPrompt({ ...promptable, freshLoad: false })).toBe(false);
    });
});

describe('pickSdrVersionOffer', () => {
    it('offers the highest-resolution SDR sibling', () => {
        const offer = pickSdrVersionOffer({
            id: 'hdr-4k',
            versions: [
                makeVersion({ id: 'hdr-4k', label: '4K HDR10', height: 2160, hdrFormat: 'HDR10' }),
                makeVersion({ id: 'sdr-720', label: '720p', height: 720 }),
                makeVersion({ id: 'sdr-1080', label: '1080p', height: 1080 }),
            ],
        });
        expect(offer?.id).toBe('sdr-1080');
    });

    it('never offers an HDR sibling — a lower HDR copy would tone-map just the same', () => {
        const offer = pickSdrVersionOffer({
            id: 'hdr-4k',
            versions: [
                makeVersion({ id: 'hdr-4k', label: '4K HDR10', height: 2160, hdrFormat: 'HDR10' }),
                makeVersion({ id: 'hdr-1080', label: '1080p HDR10', height: 1080, hdrFormat: 'HDR10' }),
            ],
        });
        expect(offer).toBeNull();
    });

    it('returns null when there are no versions at all', () => {
        expect(pickSdrVersionOffer({ id: 'solo', versions: undefined })).toBeNull();
        expect(pickSdrVersionOffer({ id: 'solo', versions: [] })).toBeNull();
    });

    it('never offers the currently playing version itself', () => {
        const offer = pickSdrVersionOffer({
            id: 'sdr-1080',
            versions: [makeVersion({ id: 'sdr-1080', label: '1080p', height: 1080 })],
        });
        expect(offer).toBeNull();
    });
});
