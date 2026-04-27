import { describe, it, expect } from 'vitest';
import type { AxiosError, InternalAxiosRequestConfig } from 'axios';
import { shouldAttemptRefresh, isRefreshRejectionInvalid } from './api';

// Helper: build a minimal AxiosError with the fields the decision helpers read.
function buildError(options: {
    status?: number;
    url?: string;
    retryFlag?: boolean;
    includeConfig?: boolean;
}): AxiosError {
    const config = options.includeConfig === false
        ? undefined
        : ({
              url: options.url ?? '/some-endpoint',
              headers: {},
              _retry: options.retryFlag,
          } as unknown as InternalAxiosRequestConfig & { _retry?: boolean });

    return {
        name: 'AxiosError',
        message: 'test',
        isAxiosError: true,
        toJSON: () => ({}),
        config,
        response: options.status
            ? ({
                  status: options.status,
                  data: {},
                  headers: {},
                  config: config!,
                  statusText: '',
              } as AxiosError['response'])
            : undefined,
    } as AxiosError;
}

describe('shouldAttemptRefresh', () => {
    it('returns true for a 401 on a regular endpoint', () => {
        const err = buildError({ status: 401, url: '/media' });
        expect(shouldAttemptRefresh(err)).toBe(true);
    });

    it('returns false for 403 (forbidden ≠ unauthenticated — must not trigger refresh)', () => {
        const err = buildError({ status: 403, url: '/media' });
        expect(shouldAttemptRefresh(err)).toBe(false);
    });

    it('returns false for 404', () => {
        const err = buildError({ status: 404, url: '/media/missing' });
        expect(shouldAttemptRefresh(err)).toBe(false);
    });

    it('returns false for 500', () => {
        const err = buildError({ status: 500, url: '/media' });
        expect(shouldAttemptRefresh(err)).toBe(false);
    });

    it('returns false for a network error (no response)', () => {
        const err = buildError({ url: '/media' });
        expect(shouldAttemptRefresh(err)).toBe(false);
    });

    it('returns false for 401 on the refresh endpoint itself (no loop)', () => {
        const err = buildError({ status: 401, url: '/auth/refresh-token' });
        expect(shouldAttemptRefresh(err)).toBe(false);
    });

    it('returns false for 401 when _retry flag is already set', () => {
        const err = buildError({ status: 401, url: '/media', retryFlag: true });
        expect(shouldAttemptRefresh(err)).toBe(false);
    });

    it('returns false when the error has no config', () => {
        const err = buildError({ status: 401, includeConfig: false });
        expect(shouldAttemptRefresh(err)).toBe(false);
    });

    it('uses a custom refresh endpoint when supplied', () => {
        const err = buildError({ status: 401, url: '/custom/refresh' });
        expect(shouldAttemptRefresh(err, '/custom/refresh')).toBe(false);
        expect(shouldAttemptRefresh(err, '/different-path')).toBe(true);
    });
});

describe('isRefreshRejectionInvalid', () => {
    it('returns true for a 401 AxiosError (refresh token rejected)', () => {
        const err = buildError({ status: 401, url: '/auth/refresh-token' });
        expect(isRefreshRejectionInvalid(err)).toBe(true);
    });

    it('returns false for a 500 AxiosError (transient — keep session)', () => {
        const err = buildError({ status: 500, url: '/auth/refresh-token' });
        expect(isRefreshRejectionInvalid(err)).toBe(false);
    });

    it('returns false for a network-level AxiosError (no response)', () => {
        const err = buildError({ url: '/auth/refresh-token' });
        expect(isRefreshRejectionInvalid(err)).toBe(false);
    });

    it('returns false for a non-axios error', () => {
        expect(isRefreshRejectionInvalid(new Error('regular error'))).toBe(false);
    });

    it('returns false for null/undefined', () => {
        expect(isRefreshRejectionInvalid(null)).toBe(false);
        expect(isRefreshRejectionInvalid(undefined)).toBe(false);
    });
});
