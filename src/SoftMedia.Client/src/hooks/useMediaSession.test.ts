import { renderHook } from '@testing-library/react';
import { StrictMode, createElement, type ReactNode } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import {
    useMediaSession,
    _resetMediaSessionRegistryForTests,
    type UseMediaSessionOptions,
} from './useMediaSession';

/**
 * R-WI-015 — Media Session hook. jsdom has no navigator.mediaSession, so a stub
 * is installed per test. Pins the three load-bearing behaviours:
 * (1) progressive enhancement — no API, no throw;
 * (2) arbitration — last player to START PLAYING owns the session, a paused
 *     owner keeps it, and ownership falls back when the owner unmounts;
 * (3) seekto routes the OS-provided absolute time to the LATEST handler
 *     (VideoPlayer's handleSeekToTime is recreated every render).
 */

type HandlerFn = ((details?: { seekTime?: number }) => void) | null;

class MediaSessionStub {
    metadata: unknown = null;
    playbackState = 'none';
    handlers = new Map<string, HandlerFn>();
    setPositionState = vi.fn();
    setActionHandler(action: string, fn: HandlerFn) {
        this.handlers.set(action, fn);
    }
}

class FakeMediaMetadata {
    title: string;
    artist: string;
    album: string;
    artwork: Array<{ src: string }>;
    constructor(init: { title?: string; artist?: string; album?: string; artwork?: Array<{ src: string }> }) {
        this.title = init.title ?? '';
        this.artist = init.artist ?? '';
        this.album = init.album ?? '';
        this.artwork = init.artwork ?? [];
    }
}

let stub: MediaSessionStub;

function installStub() {
    stub = new MediaSessionStub();
    Object.defineProperty(navigator, 'mediaSession', { value: stub, configurable: true });
    vi.stubGlobal('MediaMetadata', FakeMediaMetadata);
}

function removeApi() {
    // 'mediaSession' in navigator must be false → delete the own property.
    delete (navigator as unknown as Record<string, unknown>).mediaSession;
}

function opts(overrides: Partial<UseMediaSessionOptions> = {}): UseMediaSessionOptions {
    return {
        enabled: true,
        isPlaying: false,
        metadata: { title: 'Track', artist: 'Artist', album: 'Album', artworkUrl: '/art.jpg' },
        handlers: { onPlay: vi.fn(), onPause: vi.fn() },
        position: { duration: 100, position: 10 },
        ...overrides,
    };
}

beforeEach(() => {
    _resetMediaSessionRegistryForTests();
    installStub();
});

afterEach(() => {
    removeApi();
    vi.unstubAllGlobals();
});

describe('useMediaSession', () => {
    it('is a silent no-op when the API is absent', () => {
        removeApi();
        expect(() => {
            const { unmount } = renderHook(() => useMediaSession(opts({ isPlaying: true })));
            unmount();
        }).not.toThrow();
    });

    it('sole registered player owns the session: metadata, playbackState, handlers', () => {
        const onPlay = vi.fn();
        renderHook(() => useMediaSession(opts({ isPlaying: true, handlers: { onPlay } })));

        const meta = stub.metadata as FakeMediaMetadata;
        expect(meta.title).toBe('Track');
        expect(meta.artist).toBe('Artist');
        expect(meta.artwork).toEqual([{ src: '/art.jpg' }]);
        expect(stub.playbackState).toBe('playing');

        stub.handlers.get('play')!!();
        expect(onPlay).toHaveBeenCalledTimes(1);
        // Handlers not provided are explicitly unbound so stale ones can't linger.
        expect(stub.handlers.get('nexttrack')).toBeNull();
    });

    it('pausing keeps ownership and reports paused state (lock-screen resume)', () => {
        const { rerender } = renderHook((p: UseMediaSessionOptions) => useMediaSession(p), {
            initialProps: opts({ isPlaying: true }),
        });
        rerender(opts({ isPlaying: false }));

        expect(stub.playbackState).toBe('paused');
        expect((stub.metadata as FakeMediaMetadata).title).toBe('Track');
    });

    it('arbitration: last-to-play wins; on owner unmount the prior claimant is restored', () => {
        // Audio starts playing → owns.
        renderHook(() => useMediaSession(opts({
            isPlaying: true,
            metadata: { title: 'Song' },
        })));
        expect((stub.metadata as FakeMediaMetadata).title).toBe('Song');

        // Video mounts but is not playing yet → audio keeps the session.
        const video = renderHook((p: UseMediaSessionOptions) => useMediaSession(p), {
            initialProps: opts({ isPlaying: false, metadata: { title: 'Movie' } }),
        });
        expect((stub.metadata as FakeMediaMetadata).title).toBe('Song');

        // Video starts playing → takes over.
        video.rerender(opts({ isPlaying: true, metadata: { title: 'Movie' } }));
        expect((stub.metadata as FakeMediaMetadata).title).toBe('Movie');

        // Video unmounts → audio's session is restored, still marked playing.
        video.unmount();
        expect((stub.metadata as FakeMediaMetadata).title).toBe('Song');
        expect(stub.playbackState).toBe('playing');
    });

    it('seekto routes details.seekTime to the LATEST onSeekTo across re-renders', () => {
        const first = vi.fn();
        const second = vi.fn();
        const { rerender } = renderHook((p: UseMediaSessionOptions) => useMediaSession(p), {
            initialProps: opts({ isPlaying: true, handlers: { onSeekTo: first } }),
        });
        rerender(opts({ isPlaying: true, handlers: { onSeekTo: second } }));

        stub.handlers.get('seekto')!!({ seekTime: 321 });
        expect(first).not.toHaveBeenCalled();
        expect(second).toHaveBeenCalledWith(321);
    });

    it('handler presence appearing later rebinds (nexttrack shows up mid-playback)', () => {
        const onNext = vi.fn();
        const { rerender } = renderHook((p: UseMediaSessionOptions) => useMediaSession(p), {
            initialProps: opts({ isPlaying: true, handlers: {} }),
        });
        expect(stub.handlers.get('nexttrack')).toBeNull();

        rerender(opts({ isPlaying: true, handlers: { onNextTrack: onNext } }));
        stub.handlers.get('nexttrack')!!();
        expect(onNext).toHaveBeenCalledTimes(1);
    });

    it('position state: finite duration reported with clamping, non-finite cleared', () => {
        const { rerender } = renderHook((p: UseMediaSessionOptions) => useMediaSession(p), {
            initialProps: opts({ isPlaying: true, position: { duration: 100, position: 150 } }),
        });
        expect(stub.setPositionState).toHaveBeenLastCalledWith(
            expect.objectContaining({ duration: 100, position: 100 }), // clamped, no TypeError
        );

        // HLS while loading: element duration can be Infinity/NaN → cleared, not thrown.
        rerender(opts({ isPlaying: true, position: { duration: Infinity, position: 10 } }));
        expect(stub.setPositionState).toHaveBeenLastCalledWith();
    });

    it('last owner unmounting clears the whole session', () => {
        const { unmount } = renderHook(() => useMediaSession(opts({ isPlaying: true })));
        unmount();

        expect(stub.metadata).toBeNull();
        expect(stub.playbackState).toBe('none');
        expect(stub.setPositionState).toHaveBeenLastCalledWith();
        expect(stub.handlers.get('play')).toBeNull();
        expect(stub.handlers.get('seekto')).toBeNull();
    });

    it('enabled:false never registers or touches the session', () => {
        renderHook(() => useMediaSession(opts({ enabled: false, isPlaying: true })));
        expect(stub.metadata).toBeNull();
        expect(stub.playbackState).toBe('none');
        expect(stub.handlers.size).toBe(0);
    });

    it('switching to a NEW track while already playing re-claims ownership (contentId)', () => {
        // Review MED: with only an isPlaying edge, picking a new song from the queue
        // while a video owned the session never gave the session back to music.
        const music = renderHook((p: UseMediaSessionOptions) => useMediaSession(p), {
            initialProps: opts({ isPlaying: true, contentId: 'song-1', metadata: { title: 'Song 1' } }),
        });
        const video = renderHook((p: UseMediaSessionOptions) => useMediaSession(p), {
            initialProps: opts({ isPlaying: true, contentId: 'movie', metadata: { title: 'Movie' } }),
        });
        expect((stub.metadata as FakeMediaMetadata).title).toBe('Movie');

        music.rerender(opts({ isPlaying: true, contentId: 'song-2', metadata: { title: 'Song 2' } }));
        expect((stub.metadata as FakeMediaMetadata).title).toBe('Song 2');
        video.unmount();
        expect((stub.metadata as FakeMediaMetadata).title).toBe('Song 2'); // music stays owner
    });

    it('after fallback, actions dispatch to the RESTORED owner, not the unmounted one', () => {
        const musicPlay = vi.fn();
        const videoPlay = vi.fn();
        renderHook(() => useMediaSession(opts({ isPlaying: true, contentId: 'song', handlers: { onPlay: musicPlay } })));
        const video = renderHook(() => useMediaSession(opts({ isPlaying: true, contentId: 'movie', handlers: { onPlay: videoPlay } })));

        video.unmount();
        stub.handlers.get('play')!!();
        expect(musicPlay).toHaveBeenCalledTimes(1);
        expect(videoPlay).not.toHaveBeenCalled();
    });

    it('enabled true→false unregisters without unmount (track cleared) and clears the session', () => {
        const { rerender } = renderHook((p: UseMediaSessionOptions) => useMediaSession(p), {
            initialProps: opts({ isPlaying: true }),
        });
        rerender(opts({ enabled: false, isPlaying: false, metadata: null }));

        expect(stub.metadata).toBeNull();
        expect(stub.playbackState).toBe('none');
        expect(stub.handlers.get('play')).toBeNull();
    });

    it('survives StrictMode double-mounting with a working registry', () => {
        const wrapper = ({ children }: { children: ReactNode }) => createElement(StrictMode, null, children);
        const onPlay = vi.fn();
        const { unmount } = renderHook(
            () => useMediaSession(opts({ isPlaying: true, handlers: { onPlay } })),
            { wrapper },
        );

        expect((stub.metadata as FakeMediaMetadata).title).toBe('Track');
        expect(stub.playbackState).toBe('playing');
        stub.handlers.get('play')!!();
        expect(onPlay).toHaveBeenCalledTimes(1);

        unmount();
        expect(stub.playbackState).toBe('none'); // no ghost registrant left behind
        expect(stub.metadata).toBeNull();
    });

    it('seekto ignores fastSeek scrubber intermediates', () => {
        // Review MED: for HLS video every far seek restarts the transcode — acting on
        // each drag intermediate caused a restart storm.
        const onSeekTo = vi.fn();
        renderHook(() => useMediaSession(opts({ isPlaying: true, handlers: { onSeekTo } })));

        stub.handlers.get('seekto')!!({ seekTime: 100, fastSeek: true } as { seekTime: number });
        expect(onSeekTo).not.toHaveBeenCalled();
        stub.handlers.get('seekto')!!({ seekTime: 100 });
        expect(onSeekTo).toHaveBeenCalledWith(100);
    });

    describe('position drift throttle', () => {
        beforeEach(() => vi.useFakeTimers());
        afterEach(() => vi.useRealTimers());

        it('does not re-report an in-rhythm advance, but re-reports a >2s jump', () => {
            const { rerender } = renderHook((p: UseMediaSessionOptions) => useMediaSession(p), {
                initialProps: opts({ isPlaying: true, position: { duration: 100, position: 10 } }),
            });
            const callsAfterMount = stub.setPositionState.mock.calls.length;

            // 1s later the playhead is at 11 — exactly what the UA extrapolates; no call.
            vi.advanceTimersByTime(1000);
            rerender(opts({ isPlaying: true, position: { duration: 100, position: 11 } }));
            expect(stub.setPositionState.mock.calls.length).toBe(callsAfterMount);

            // A seek: 1s later the playhead is at 40 — discontinuity; must re-report.
            vi.advanceTimersByTime(1000);
            rerender(opts({ isPlaying: true, position: { duration: 100, position: 40 } }));
            expect(stub.setPositionState.mock.calls.length).toBe(callsAfterMount + 1);
            expect(stub.setPositionState).toHaveBeenLastCalledWith(
                expect.objectContaining({ position: 40 }),
            );
        });

        it('re-reports ANY movement while paused (UA does not extrapolate paused positions)', () => {
            // Review LOW: a sub-2s nudge while paused was swallowed and stayed wrong forever.
            const { rerender } = renderHook((p: UseMediaSessionOptions) => useMediaSession(p), {
                initialProps: opts({ isPlaying: false, position: { duration: 100, position: 10 } }),
            });
            const baseline = stub.setPositionState.mock.calls.length;

            rerender(opts({ isPlaying: false, position: { duration: 100, position: 11 } }));
            expect(stub.setPositionState.mock.calls.length).toBe(baseline + 1);
            expect(stub.setPositionState).toHaveBeenLastCalledWith(
                expect.objectContaining({ position: 11 }),
            );

            // Unmoved rerender while paused → no call.
            rerender(opts({ isPlaying: false, position: { duration: 100, position: 11 } }));
            expect(stub.setPositionState.mock.calls.length).toBe(baseline + 1);
        });
    });
});
