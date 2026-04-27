import axios from 'axios';
import MockAdapter from 'axios-mock-adapter';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import api from './api';
import { useAuthStore } from '../store/authStore';

/**
 * Todo 05 integration tests: exercise the full axios interceptor flow —
 * single-flight refresh dedup, replay-original-request on successful refresh,
 * logout only when refresh is explicitly rejected, and 403 pass-through.
 *
 * Uses axios-mock-adapter to intercept requests on both the default axios
 * instance (used by refreshAccessToken) and the shared `api` instance
 * (used for the original + replayed request).
 */
describe('api interceptor — integration', () => {
    let apiMock: MockAdapter;
    let refreshMock: MockAdapter;

    const fakeUser = { id: 'u1', username: 'alice', role: 'User' } as const;

    beforeEach(() => {
        apiMock = new MockAdapter(api);
        refreshMock = new MockAdapter(axios);
        // Seed a valid-ish token so the request interceptor attaches it.
        useAuthStore.getState().login(fakeUser, 'initial-access-token');
    });

    afterEach(() => {
        apiMock.restore();
        refreshMock.restore();
        useAuthStore.getState().logout();
        vi.clearAllTimers();
    });

    it('403 does not trigger refresh and does not logout', async () => {
        apiMock.onGet('/protected').reply(403, { error: 'forbidden' });
        refreshMock.onPost('/api/v1/auth/refresh-token').reply(() => {
            throw new Error('refresh must not be called on 403');
        });

        await expect(api.get('/protected')).rejects.toMatchObject({
            response: { status: 403 },
        });

        // Still logged in
        expect(useAuthStore.getState().isAuthenticated).toBe(true);
        // Refresh was never called
        expect(refreshMock.history.post).toHaveLength(0);
    });

    it('401 on refresh endpoint itself does not loop', async () => {
        apiMock.onPost('/auth/refresh-token').reply(401);

        await expect(api.post('/auth/refresh-token')).rejects.toMatchObject({
            response: { status: 401 },
        });

        // No refresh side-call from the interceptor because the URL is excluded.
        expect(refreshMock.history.post).toHaveLength(0);
    });

    it('successful refresh replays original request and returns the replayed response', async () => {
        // First call: 401. Replay (after refresh) should succeed.
        let callCount = 0;
        apiMock.onGet('/protected').reply(() => {
            callCount++;
            if (callCount === 1) return [401, { error: 'expired' }];
            return [200, { data: 'ok' }];
        });

        refreshMock.onPost('/api/v1/auth/refresh-token').reply(200, {
            accessToken: 'refreshed-access-token',
            user: fakeUser,
        });

        const response = await api.get('/protected');

        expect(response.status).toBe(200);
        expect(response.data).toEqual({ data: 'ok' });
        expect(useAuthStore.getState().token).toBe('refreshed-access-token');
        expect(callCount).toBe(2);
        expect(refreshMock.history.post).toHaveLength(1);
    });

    it('refresh failure with 401 logs the user out', async () => {
        apiMock.onGet('/protected').reply(401);
        refreshMock.onPost('/api/v1/auth/refresh-token').reply(401);

        await expect(api.get('/protected')).rejects.toMatchObject({
            response: { status: 401 },
        });

        expect(useAuthStore.getState().isAuthenticated).toBe(false);
        expect(useAuthStore.getState().token).toBeNull();
    });

    it('refresh failure with 500 forwards original error but keeps session', async () => {
        apiMock.onGet('/protected').reply(401);
        refreshMock.onPost('/api/v1/auth/refresh-token').reply(500);

        await expect(api.get('/protected')).rejects.toMatchObject({
            response: { status: 401 },
        });

        // Session preserved — transient error shouldn't kick user out.
        expect(useAuthStore.getState().isAuthenticated).toBe(true);
        expect(useAuthStore.getState().token).toBe('initial-access-token');
    });

    it('multiple concurrent 401s trigger exactly one refresh', async () => {
        apiMock.onGet('/a').replyOnce(401).onGet('/a').reply(200, { id: 'a' });
        apiMock.onGet('/b').replyOnce(401).onGet('/b').reply(200, { id: 'b' });
        apiMock.onGet('/c').replyOnce(401).onGet('/c').reply(200, { id: 'c' });

        refreshMock.onPost('/api/v1/auth/refresh-token').reply(200, {
            accessToken: 'fresh-token',
            user: fakeUser,
        });

        const [ra, rb, rc] = await Promise.all([
            api.get('/a'),
            api.get('/b'),
            api.get('/c'),
        ]);

        expect(ra.data).toEqual({ id: 'a' });
        expect(rb.data).toEqual({ id: 'b' });
        expect(rc.data).toEqual({ id: 'c' });

        // Single-flight: exactly one refresh POST across the three concurrent 401s.
        expect(refreshMock.history.post).toHaveLength(1);
    });
});
