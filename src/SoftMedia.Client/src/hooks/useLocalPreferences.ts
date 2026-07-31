import { useState, useEffect, useCallback } from 'react';
import { useAuthStore } from '../store/authStore';

export interface LocalPreferences {
    defaultStreamingQuality: string;
    maxBitrate: string;
    dataSaverMode: string;
    // Audio playback settings
    maxAudioBitrate: string;
    // New fields for Client Settings
    audioLanguage: string;
    subtitleLanguage: string;
    autoSelectSubtitle: string; // 'true' | 'false'
    burnSubtitles: string; // 'auto' | 'always'
    // Skip-segment behavior (Plex-style per-device preference)
    autoSkipIntros: string;  // 'true' | 'false'
    autoSkipCredits: string; // 'true' | 'false'
    // R-WI-018 — subtitle appearance (device-level, like all caption settings)
    subtitleFontSize: string;   // percent: '75' | '100' | '125' | '150'
    subtitleColor: string;      // 'white' | 'yellow' | 'cyan' | 'green'
    subtitleBgOpacity: string;  // '0' | '0.5' | '0.75' | '1'
    subtitleEdgeStyle: string;  // 'none' | 'outline' | 'shadow'
    // Photo slideshow entrance transition (per-device, like all viewing prefs)
    slideshowTransition: string; // 'none' | 'fade' | 'zoom' | 'slide'
}

const BASE_PREFERENCES_KEY = 'softmedia_preferences';

const defaultPreferences: LocalPreferences = {
    defaultStreamingQuality: 'auto',
    maxBitrate: '0', // 0 = unlimited
    dataSaverMode: 'false',
    maxAudioBitrate: '0', // 0 = original/unlimited
    audioLanguage: 'en',
    subtitleLanguage: 'off',
    autoSelectSubtitle: 'true',
    burnSubtitles: 'auto',
    autoSkipIntros: 'false',
    autoSkipCredits: 'false',
    subtitleFontSize: '100',
    subtitleColor: 'white',
    subtitleBgOpacity: '0.75',
    subtitleEdgeStyle: 'none',
    slideshowTransition: 'fade',
};

/**
 * Hook for managing localStorage-based preferences.
 * These are device-specific settings, strictly isolated per user.
 */
export function useLocalPreferences() {
    const user = useAuthStore((state) => state.user);
    const userId = user?.id || 'guest';
    const storageKey = `${BASE_PREFERENCES_KEY}_${userId}`;

    const [preferences, setPreferences] = useState<LocalPreferences>(() => {
        try {
            const stored = localStorage.getItem(storageKey);
            if (stored) {
                return { ...defaultPreferences, ...JSON.parse(stored) };
            }
        } catch (e) {
            console.error('Failed to load local preferences:', e);
        }
        return defaultPreferences;
    });

    // Reload when the user changes — during render, so the previous user's
    // subtitle/theme prefs never apply for a frame after switching accounts.
    // Reading localStorage here is fine: it's synchronous and stable between
    // renders, which is all render-time adjustment requires.
    const [loadedKey, setLoadedKey] = useState(storageKey);
    if (storageKey !== loadedKey) {
        setLoadedKey(storageKey);
        try {
            const stored = localStorage.getItem(storageKey);
            setPreferences(stored
                ? { ...defaultPreferences, ...JSON.parse(stored) }
                : defaultPreferences);
        } catch {
            setPreferences(defaultPreferences);
        }
    }

    // Persist to localStorage whenever preferences change (using the current user's key)
    useEffect(() => {
        try {
            localStorage.setItem(storageKey, JSON.stringify(preferences));
        } catch (e) {
            console.error('Failed to save local preferences:', e);
        }
    }, [preferences, storageKey]);

    const updatePreference = useCallback(<K extends keyof LocalPreferences>(
        key: K,
        value: LocalPreferences[K]
    ) => {
        setPreferences(prev => ({ ...prev, [key]: value }));
    }, []);

    const resetToDefaults = useCallback(() => {
        setPreferences(defaultPreferences);
    }, []);

    return {
        preferences,
        updatePreference,
        resetToDefaults,
    };
}
