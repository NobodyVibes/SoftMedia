import { describe, it, expect, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useLocalPreferences } from './useLocalPreferences';
import { useAuthStore } from '../store/authStore';

/**
 * QS-WI-007 — the web client's first-run seed IS the hook's defaults (device-local
 * storage, nothing admin-managed): a fresh device with empty localStorage must ask for
 * Auto quality with no bitrate pin. Desktop/TV/mobile get different seeds when those
 * clients exist — that's the per-client checklist in the streaming-quality plan §3,
 * NOT this hook.
 */
describe('useLocalPreferences first-run defaults', () => {
    beforeEach(() => {
        localStorage.clear();
        useAuthStore.setState({ user: null });
    });

    it('seeds Auto quality / unlimited bitrate on first run (QS-WI-007, web device class)', () => {
        const { result } = renderHook(() => useLocalPreferences());

        expect(result.current.preferences.defaultStreamingQuality).toBe('auto');
        expect(result.current.preferences.maxBitrate).toBe('0');
        expect(result.current.preferences.dataSaverMode).toBe('false');
    });

    it('seeds Media Tips ON and the HDR warning un-dismissed (QS-WI-005/011)', () => {
        const { result } = renderHook(() => useLocalPreferences());

        expect(result.current.preferences.mediaTipsEnabled).toBe('true');
        expect(result.current.preferences.showHdrTranscodeWarning).toBe('true');
    });

    it('a stored blob from an older version keeps the new defaults for missing keys', () => {
        // Pre-QS-WI-011 devices have no mediaTipsEnabled key — the merge must not
        // resurrect `undefined` (which would read as tips-off via !== checks).
        localStorage.setItem('softmedia_preferences_guest',
            JSON.stringify({ defaultStreamingQuality: '1080p' }));

        const { result } = renderHook(() => useLocalPreferences());

        expect(result.current.preferences.defaultStreamingQuality).toBe('1080p');
        expect(result.current.preferences.mediaTipsEnabled).toBe('true');
    });

    it('persists updates under the per-user key', () => {
        const { result } = renderHook(() => useLocalPreferences());

        act(() => result.current.updatePreference('mediaTipsEnabled', 'false'));

        expect(JSON.parse(localStorage.getItem('softmedia_preferences_guest')!).mediaTipsEnabled)
            .toBe('false');
    });
});
