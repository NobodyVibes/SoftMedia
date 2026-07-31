import { useState, useEffect, useCallback } from 'react';
import { type MediaItem, type TrackInfo } from '../types';
import { type LocalPreferences } from './useLocalPreferences';
import { normalizeLanguage } from '../lib/utils';
import { useAuthStore } from '../store/authStore';

interface UseTrackSelectionProps {
    item: MediaItem;
    token: string | null;
    localPrefs: LocalPreferences;
}

interface SavedTrackPref {
    language?: string;
    title?: string;
    isOff?: boolean;
}

export function useTrackSelection({ item, token, localPrefs }: UseTrackSelectionProps) {
    const [audioTracks, setAudioTracks] = useState<TrackInfo[]>([]);
    const [subtitleTracks, setSubtitleTracks] = useState<TrackInfo[]>([]);
    const [selectedAudioTrack, setSelectedAudioTrack] = useState<number | null>(null);
    const [selectedSubtitleTrack, setSelectedSubtitleTrack] = useState<number | null>(null);

    // Helper to get storage key for "Last Used" track
    const getLastUsedKey = useCallback((type: 'audio' | 'subtitle') => {
        const userId = useAuthStore.getState().user?.id || 'guest';
        const contentId = item.seriesId || item.id; // Share prefs across series
        return `sm_last_track_v2_${userId}_${contentId}_${type}`; // v2 key to avoid conflict/migration issues
    }, [item.id, item.seriesId]);

    // Save preference to local storage
    const saveLastUsedTrack = useCallback((type: 'audio' | 'subtitle', track: TrackInfo | null | -1) => {
        try {
            const key = getLastUsedKey(type);

            if (track === -1 || track === null) {
                // Explicitly Off (subtitles only usually)
                const pref: SavedTrackPref = { isOff: true };
                localStorage.setItem(key, JSON.stringify(pref));
                return;
            }

            const pref: SavedTrackPref = {
                language: track.language,
                title: track.title,
                isOff: false
            };
            localStorage.setItem(key, JSON.stringify(pref));
        } catch (e) {
            console.error('Failed to save last used track:', e);
        }
    }, [getLastUsedKey]);

    useEffect(() => {
        if (!item.id || !token) return;

        let isMounted = true;

        const fetchTracks = async () => {
            try {
                const response = await fetch(`/api/media/${item.id}/tracks`, {
                    headers: { Authorization: `Bearer ${token}` }
                });

                if (!response.ok) return;
                const data = await response.json();

                if (!isMounted) return;

                const audios: TrackInfo[] = data.audioTracks || [];
                const subs: TrackInfo[] = data.subtitleTracks || [];

                setAudioTracks(audios);
                setSubtitleTracks(subs);

                // --- AUDIO SELECTION LOGIC ---
                let bestAudioIndex = -1;

                // 1. Check "Last Used" (Saved per series/item)
                try {
                    const lastAudioJson = localStorage.getItem(getLastUsedKey('audio'));
                    if (lastAudioJson) {
                        const pref = JSON.parse(lastAudioJson) as SavedTrackPref;

                        // Priority A: Exact Match (Language + Title)
                        // This handles "English (Stereo)" vs "English (5.1)" distinction
                        let match = audios.find(t =>
                            normalizeLanguage(t.language || '') === normalizeLanguage(pref.language || '') &&
                            t.title === pref.title
                        );

                        // Priority B: Language Match Only
                        if (!match && pref.language) {
                            const targetLang = normalizeLanguage(pref.language);
                            match = audios.find(t => normalizeLanguage(t.language || '') === targetLang)
                                || audios.find(t => t.language?.toLowerCase().startsWith(targetLang.toLowerCase()));
                        }

                        if (match) {
                            bestAudioIndex = match.index;
                        }
                    }
                } catch (e) {
                    console.warn('Failed to parse last used audio pref', e);
                }

                // 2. Check "Global Audio Language Preference"
                if (bestAudioIndex === -1 && localPrefs.audioLanguage) {
                    const targetLang = normalizeLanguage(localPrefs.audioLanguage);
                    const match = audios.find(t => normalizeLanguage(t.language || '') === targetLang)
                        || audios.find(t => t.language?.toLowerCase().startsWith(targetLang.toLowerCase()));
                    if (match) bestAudioIndex = match.index;
                }

                // 3. Default Flag
                if (bestAudioIndex === -1) {
                    const def = audios.find(t => t.isDefault);
                    if (def) bestAudioIndex = def.index;
                }

                // 4. First Track
                if (bestAudioIndex === -1 && audios.length > 0) {
                    bestAudioIndex = audios[0].index;
                }

                setSelectedAudioTrack(bestAudioIndex);

                // --- SUBTITLE SELECTION LOGIC ---
                let bestSubIndex = -1; // -1 means off

                // 1. Check "Last Used"
                try {
                    const lastSubJson = localStorage.getItem(getLastUsedKey('subtitle'));
                    if (lastSubJson) {
                        const pref = JSON.parse(lastSubJson) as SavedTrackPref;

                        if (pref.isOff) {
                            bestSubIndex = -1;
                        } else {
                            // Priority A: Exact Match
                            let match = subs.find(t =>
                                normalizeLanguage(t.language || '') === normalizeLanguage(pref.language || '') &&
                                t.title === pref.title
                            );

                            // Priority B: Language Match
                            if (!match && pref.language) {
                                const targetLang = normalizeLanguage(pref.language);
                                match = subs.find(t => normalizeLanguage(t.language || '') === targetLang)
                                    || subs.find(t => t.language?.toLowerCase().startsWith(targetLang.toLowerCase()));
                            }

                            if (match) {
                                bestSubIndex = match.index;
                            }
                        }
                    } else {
                        // If no last used, Treat as NULL to allow Global Pref fallback
                        // (Using a flag to indicate 'found preference')
                        bestSubIndex = -2; // Temporary marker for "not found"
                    }
                } catch {
                    bestSubIndex = -2;
                }

                // 2. Check "Global Subtitle Language Preference" (only if Last Used wasn't set)
                if (bestSubIndex === -2 && localPrefs.subtitleLanguage) {
                    bestSubIndex = -1; // Reset to Off default
                    const targetLang = normalizeLanguage(localPrefs.subtitleLanguage);
                    const match = subs.find(t => normalizeLanguage(t.language || '') === targetLang)
                        || subs.find(t => t.language?.toLowerCase().startsWith(targetLang.toLowerCase()));
                    if (match) bestSubIndex = match.index;
                } else if (bestSubIndex === -2) {
                    bestSubIndex = -1; // Reset to Off default
                }

                // 3. Forced/Default Flag (If still undecided or Off?)
                // If global pref result was "Off" (-1), we might still want to show forced subs?
                // Logic: If user explicit global pref meant "Off" (or no match), we usually check forced.
                if (bestSubIndex === -1) {
                    const forced = subs.find(t => t.isDefault || t.title?.toLowerCase().includes('forced'));
                    if (forced) bestSubIndex = forced.index;
                }

                setSelectedSubtitleTrack(bestSubIndex);

            } catch (error) {
                console.error('Error fetching tracks:', error);
            }
        };

        fetchTracks();

        return () => { isMounted = false; };
    }, [item.id, item.seriesId, token, localPrefs.audioLanguage, localPrefs.subtitleLanguage, getLastUsedKey]);

    return {
        audioTracks,
        subtitleTracks,
        selectedAudioTrack,
        selectedSubtitleTrack,
        setSelectedAudioTrack: (index: number) => {
            setSelectedAudioTrack(index);
            const track = audioTracks.find(t => t.index === index) || null;
            saveLastUsedTrack('audio', track);
        },
        setSelectedSubtitleTrack: (index: number) => {
            setSelectedSubtitleTrack(index);
            if (index === -1) {
                saveLastUsedTrack('subtitle', -1);
            } else {
                const track = subtitleTracks.find(t => t.index === index) || null;
                saveLastUsedTrack('subtitle', track);
            }
        }
    };
}

