import { type ClassValue, clsx } from "clsx";
import { twMerge } from "tailwind-merge";

export function cn(...inputs: ClassValue[]) {
    return twMerge(clsx(inputs));
}

export function normalizeLanguage(lang: string | undefined): string {
    if (!lang) return '';
    const clean = lang.toLowerCase().trim();

    // Map common 3-letter codes and full names to 2-letter codes
    const map: Record<string, string> = {
        'eng': 'en', 'english': 'en', 'usa': 'en',
        'spa': 'es', 'spanish': 'es', 'esp': 'es',
        'fra': 'fr', 'french': 'fr', 'fre': 'fr',
        'deu': 'de', 'german': 'de', 'ger': 'de',
        'ita': 'it', 'italian': 'it',
        'zho': 'zh', 'chi': 'zh', 'chinese': 'zh',
        'jpn': 'ja', 'japanese': 'ja',
        'ara': 'ar', 'arabic': 'ar',
        'por': 'pt', 'portuguese': 'pt',
        'rus': 'ru', 'russian': 'ru',
        'pol': 'pl', 'polish': 'pl',
        'tur': 'tr', 'turkish': 'tr',
        'swe': 'sv', 'swedish': 'sv',
        'kor': 'ko', 'korean': 'ko',
    };

    return map[clean] || clean;
}

export function formatDuration(seconds: number): string {
    const minutes = Math.floor(seconds / 60);
    const remainingSeconds = Math.floor(seconds % 60);
    return `${minutes}:${remainingSeconds.toString().padStart(2, '0')}`;
}

/**
 * Runtime-style label ("2h 15m 3s" / "5m 3s") from raw seconds — the exact format the
 * server's removed `duration` string used (SR-WI-063), so cards/detail headers render
 * unchanged from `durationSeconds`. Returns null for missing/zero durations so callers
 * can conditionally render the pill.
 */
export function formatRuntime(seconds: number | null | undefined): string | null {
    if (!seconds || seconds <= 0) return null;
    const total = Math.floor(seconds);
    const h = Math.floor(total / 3600);
    const m = Math.floor((total % 3600) / 60);
    const s = total % 60;
    return h >= 1 ? `${h}h ${m}m ${s}s` : `${m}m ${s}s`;
}

/**
 * Coarse "how long ago" label for timestamps whose exact value doesn't matter to
 * the reader (a playlist's last edit, say). Deliberately stops at weeks and falls
 * back to a locale date beyond that — "37 weeks ago" is harder to place than the
 * date itself. Returns null for missing or unparsable input so callers can drop
 * the label rather than render "Invalid Date".
 */
export function formatRelativeTime(timestamp: string | null | undefined): string | null {
    if (!timestamp) return null;
    const then = new Date(timestamp);
    const ms = then.getTime();
    if (Number.isNaN(ms)) return null;

    // Future timestamps (clock skew between server and client) read as "just now"
    // rather than a negative interval.
    const seconds = Math.max(0, (Date.now() - ms) / 1000);
    if (seconds < 60) return 'just now';

    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) return `${minutes}m ago`;

    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `${hours}h ago`;

    const days = Math.floor(hours / 24);
    if (days === 1) return 'yesterday';
    if (days < 7) return `${days} days ago`;

    const weeks = Math.floor(days / 7);
    if (weeks < 5) return weeks === 1 ? 'last week' : `${weeks} weeks ago`;

    return then.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
}

/**
 * Whether an interval-hours settings value will actually ENABLE the schedule on the server.
 * The server parses these with int.TryParse and treats anything unparsable (empty string,
 * "2.5", "2e5") or <= 0 as disabled — so the UI hint next to the input must apply the exact
 * same rule, or an admin could save "2.5", see "hours", and believe a schedule is running
 * when it silently never fires (R-WI-008 review finding).
 */
export function isIntervalHoursEnabled(value: string): boolean {
    if (!/^-?\d+$/.test(value.trim())) return false; // int.TryParse semantics: integers only
    const n = Number(value);
    return Number.isSafeInteger(n) && n > 0;
}
