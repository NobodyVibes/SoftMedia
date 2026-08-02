import type { MediaItem, MediaVersion } from '../types';

/**
 * QS-WI-005 — HDR-transcode guardrail helpers.
 *
 * "Sitting" (plan §7 definition, mechanism adapted to the real player): consecutive
 * automatic next-episode transitions without manual navigation. Auto-advance NAVIGATES to
 * `/play/{next}` (the player remounts), so the sitting state cannot live in a ref — it is
 * handed across the navigation via sessionStorage (per-tab, gone when the tab closes):
 *
 *  - the NextEpisodeOverlay handlers call {@link markAutoAdvance} right before navigating;
 *  - the next player mount calls {@link beginPlaybackSitting} exactly once, which CONSUMES
 *    the hand-off flag: if it was present the sitting continues (a prior "Play anyway"
 *    still covers this episode), otherwise this is a manual play and any previous answer
 *    is cleared.
 *
 * Any manual play — detail page, episode row, version switch, the player's own prev/next
 * buttons — never sets the hand-off flag, so it starts a new sitting and may prompt again.
 */

const AUTO_ADVANCE_KEY = 'softmedia_hdr_autoadvance';
const SITTING_ACK_KEY = 'softmedia_hdr_sitting_ack';

/** Call immediately before an automatic next-episode navigation. */
export function markAutoAdvance(): void {
    try { sessionStorage.setItem(AUTO_ADVANCE_KEY, '1'); } catch { /* storage unavailable */ }
}

/**
 * Call once per playback start (before deciding whether to prompt). Returns true when a
 * previous prompt answer still covers this playback (unbroken auto-advance sitting).
 */
export function beginPlaybackSitting(): boolean {
    try {
        const isContinuation = sessionStorage.getItem(AUTO_ADVANCE_KEY) === '1';
        sessionStorage.removeItem(AUTO_ADVANCE_KEY);
        if (!isContinuation) {
            sessionStorage.removeItem(SITTING_ACK_KEY);
            return false;
        }
        return sessionStorage.getItem(SITTING_ACK_KEY) === '1';
    } catch {
        return false;
    }
}

/** Call when the user answers "Play anyway" so the rest of the binge doesn't re-prompt. */
export function acknowledgeSitting(): void {
    try { sessionStorage.setItem(SITTING_ACK_KEY, '1'); } catch { /* storage unavailable */ }
}

/**
 * QS-WI-011 — the single gate deciding whether the pre-play HDR guardrail shows.
 * VideoPlayer feeds it the plan facts and the device-local preferences; keeping the
 * decision pure (and here, not inline in the player) pins the suppression contract:
 *
 *  - `block` (admin BlockHdrTranscode) ALWAYS shows the dialog — neither Media Tips nor
 *    "Never show again" can bypass an admin rule (and the server refuses the transcode
 *    with 403 regardless, so suppression couldn't bypass it anyway);
 *  - `warn` is an unsolicited surface, so it is suppressed by Media Tips being off, by
 *    the per-prompt "Never show again" flag, or by an earlier answer within an unbroken
 *    auto-advance sitting;
 *  - the user-invoked TranscodeExplanationModal is NOT routed through this gate at all —
 *    Media Tips governs what SoftMedia volunteers, never what the user asks for.
 */
export function shouldShowHdrPrompt(opts: {
    /** The plan's HdrTranscodePolicy: 'warn' | 'block' | null/undefined. */
    policy: string | null | undefined;
    /** The plan's ToneMapPlanned fact — the prompt keys off the PLAN, never the file. */
    toneMapPlanned: boolean;
    /** Fresh loads only; mid-session re-plans (subtitle/quality change) are the same play. */
    freshLoad: boolean;
    mediaTipsEnabled: boolean;
    /** The per-prompt "Never show again" flag (showHdrTranscodeWarning === 'false'). */
    neverShowAgain: boolean;
    /** A prior answer still covers this playback (unbroken auto-advance sitting). */
    sittingCovered: boolean;
}): boolean {
    if (!opts.toneMapPlanned || !opts.policy || !opts.freshLoad) return false;
    if (opts.policy === 'block') return true;
    return opts.mediaTipsEnabled && !opts.neverShowAgain && !opts.sittingCovered;
}

/**
 * The version the guardrail may OFFER (never auto-pick — standing owner decision): the
 * best non-HDR sibling in the version group. Only an SDR copy actually avoids the
 * tone-map — a lower-resolution HDR copy would wash out just the same — so HDR siblings
 * are never offered. Highest resolution wins among SDR candidates.
 */
export function pickSdrVersionOffer(item: Pick<MediaItem, 'id' | 'versions'>): MediaVersion | null {
    const candidates = (item.versions ?? []).filter(v => v.id !== item.id && !v.hdrFormat);
    if (candidates.length === 0) return null;
    return [...candidates].sort((a, b) => (b.height ?? 0) - (a.height ?? 0))[0];
}
