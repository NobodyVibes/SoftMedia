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

    // Update state when user changes (to load that user's prefs)
    useEffect(() => {
        const newKey = `${BASE_PREFERENCES_KEY}_${userId}`;
        try {
            const stored = localStorage.getItem(newKey);
            if (stored) {
                setPreferences({ ...defaultPreferences, ...JSON.parse(stored) });
            } else {
                setPreferences(defaultPreferences);
            }
        } catch (e) {
            setPreferences(defaultPreferences);
        }
    }, [userId]);

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
