/**
 * Pure cast-readiness logic (CC-WI-005). Kept free of the Cast SDK / window globals so it can
 * be unit-tested: given the observable signals, it produces the human-facing diagnostic.
 */

export type CastState = 'unknown' | 'no-devices' | 'available' | 'connecting' | 'connected';
export type CastUnavailableReason = 'insecure-context' | 'no-sdk' | null;

export interface CastReadinessInput {
    /** window.isSecureContext */
    isSecureContext: boolean;
    /** window.location.hostname */
    hostname: string;
    /** SDK initialised (Cast API present) */
    isCastAvailable: boolean;
    /** From CastContext.getCastState() */
    castState: CastState;
    /** Why the SDK is unavailable, when it is */
    castUnavailableReason: CastUnavailableReason;
}

export interface CastCheck {
    label: string;
    status: 'ok' | 'warn' | 'fail';
    detail: string;
}

export interface CastReadiness {
    /** True only when a cast could actually start and reach the device. */
    ready: boolean;
    headline: string;
    checks: CastCheck[];
}

export function isLoopbackHost(hostname: string): boolean {
    return hostname === 'localhost'
        || hostname === '0.0.0.0'
        || hostname === '::1'
        || hostname.startsWith('127.');
}

export function describeCastReadiness(i: CastReadinessInput): CastReadiness {
    const loopback = isLoopbackHost(i.hostname);
    const checks: CastCheck[] = [];

    // 1. Secure context / reachable origin.
    if (loopback) {
        checks.push({
            label: 'Reachable HTTPS address',
            status: 'warn',
            detail: `You're on "${i.hostname}". The cast button can appear here, but the TV can't fetch a localhost stream — open SoftMedia by its HTTPS domain (e.g. https://media.example.com) to actually cast.`,
        });
    } else if (i.isSecureContext) {
        checks.push({ label: 'Reachable HTTPS address', status: 'ok', detail: `Secure origin (${i.hostname}).` });
    } else {
        checks.push({
            label: 'Reachable HTTPS address',
            status: 'fail',
            detail: 'This page is not served over HTTPS, so Chrome disables casting entirely. Put SoftMedia behind HTTPS with a publicly-trusted certificate (see the casting docs).',
        });
    }

    // 2. Cast supported by this browser.
    if (i.isCastAvailable) {
        checks.push({ label: 'Casting supported by this browser', status: 'ok', detail: 'The Google Cast SDK is active.' });
    } else if (i.castUnavailableReason === 'insecure-context') {
        checks.push({ label: 'Casting supported by this browser', status: 'fail', detail: 'Disabled because the page is not HTTPS (see above).' });
    } else if (i.castUnavailableReason === 'no-sdk') {
        checks.push({ label: 'Casting supported by this browser', status: 'fail', detail: 'This browser has no Google Cast support. Use desktop Chrome or Edge.' });
    } else {
        checks.push({ label: 'Casting supported by this browser', status: 'warn', detail: 'Still initialising…' });
    }

    // 3. A Google Cast device on the network — the part most people get wrong.
    if (!i.isCastAvailable) {
        checks.push({ label: 'A Google Cast device on your network', status: 'warn', detail: 'Can’t check until casting is available.' });
    } else if (i.castState === 'no-devices') {
        checks.push({
            label: 'A Google Cast device on your network',
            status: 'fail',
            detail: 'No Google Cast devices found. Smart TVs such as LG (webOS) and Samsung (Tizen) are NOT Cast receivers — you need a Chromecast or Google TV (it can plug into the TV’s HDMI port).',
        });
    } else if (i.castState === 'connected' || i.castState === 'connecting') {
        checks.push({ label: 'A Google Cast device on your network', status: 'ok', detail: 'Connected to a Cast device.' });
    } else {
        checks.push({ label: 'A Google Cast device on your network', status: 'ok', detail: 'At least one Cast device is available.' });
    }

    const ready = i.isCastAvailable
        && !loopback
        && i.castState !== 'no-devices'
        && i.castState !== 'unknown';

    let headline: string;
    if (ready) headline = 'Ready to cast.';
    else if (checks.some((c) => c.status === 'fail')) headline = 'Casting isn’t available yet — here’s why:';
    else headline = 'Casting is almost ready:';

    return { ready, headline, checks };
}
