/**
 * SR-WI-026 — pure helpers for the video player's error/recovery policy, split
 * out of VideoPlayer.tsx so the retry math is unit-testable without mounting
 * the full player.
 */

/**
 * How many fatal-network `startLoad()` retries the player attempts before
 * declaring the connection lost (terminal state with a Retry action).
 */
export const MAX_NETWORK_RETRIES = 6;

/**
 * Exponential backoff for fatal-network retries: 1s, 2s, 4s, 8s, 8s, 8s —
 * the full budget of MAX_NETWORK_RETRIES attempts spans ~31 seconds.
 * `attempt` is 1-based (the attempt number just consumed).
 */
export function networkRetryDelayMs(attempt: number): number {
    const bounded = Math.max(1, attempt);
    return Math.min(1000 * 2 ** (bounded - 1), 8000);
}

/**
 * Parse a Retry-After response header (delta-seconds form) from a 429.
 * Missing/unparsable values (including the HTTP-date form) fall back to
 * `fallbackSeconds`; the result is clamped to `maxSeconds` so a buggy header
 * can't park the player for an hour.
 */
export function parseRetryAfterSeconds(
    header: string | null | undefined,
    fallbackSeconds = 10,
    maxSeconds = 60,
): number {
    const parsed = Number.parseInt(header ?? '', 10);
    if (!Number.isFinite(parsed) || parsed <= 0) return fallbackSeconds;
    return Math.min(parsed, maxSeconds);
}
