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
