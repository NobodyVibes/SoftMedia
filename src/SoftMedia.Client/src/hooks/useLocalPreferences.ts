import { useState, useEffect, useCallback } from 'react';

export interface LocalPreferences {
    defaultStreamingQuality: string;
    maxBitrate: string;
    dataSaverMode: string;
}

const LOCAL_PREFERENCES_KEY = 'softmedia_local_preferences';

const defaultPreferences: LocalPreferences = {
    defaultStreamingQuality: 'auto',
    maxBitrate: '0', // 0 = unlimited
    dataSaverMode: 'false',
};

/**
 * Hook for managing localStorage-based preferences.
 * These are device-specific settings that don't sync across devices.
 */
export function useLocalPreferences() {
    const [preferences, setPreferences] = useState<LocalPreferences>(() => {
        try {
            const stored = localStorage.getItem(LOCAL_PREFERENCES_KEY);
            if (stored) {
                return { ...defaultPreferences, ...JSON.parse(stored) };
            }
        } catch (e) {
            console.error('Failed to load local preferences:', e);
        }
        return defaultPreferences;
    });

    // Persist to localStorage whenever preferences change
    useEffect(() => {
        try {
            localStorage.setItem(LOCAL_PREFERENCES_KEY, JSON.stringify(preferences));
        } catch (e) {
            console.error('Failed to save local preferences:', e);
        }
    }, [preferences]);

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
