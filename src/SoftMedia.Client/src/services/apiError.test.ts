import { describe, expect, it } from 'vitest';
import { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from 'axios';
import { extractApiError, extractApiErrorCode } from './apiError';

/** Build a real AxiosError carrying the given response body. */
function axiosErrorWith(data: unknown, status = 400): AxiosError {
    const config = { headers: {} } as InternalAxiosRequestConfig;
    const response = { data, status, statusText: '', headers: {}, config } as AxiosResponse;
    return new AxiosError('Request failed', 'ERR_BAD_REQUEST', config, {}, response);
}

describe('extractApiError (SR-WI-061)', () => {
    it('prefers ProblemDetails detail over title', () => {
        const err = axiosErrorWith({
            title: 'Not found',
            status: 404,
            detail: 'Episode not found or no next episode',
            traceId: '00-abc-00',
        }, 404);
        expect(extractApiError(err, 'fallback')).toBe('Episode not found or no next episode');
    });

    it('falls back to ProblemDetails title when detail is absent', () => {
        const err = axiosErrorWith({ title: 'An error occurred while processing your request.', status: 500 }, 500);
        expect(extractApiError(err, 'fallback')).toBe('An error occurred while processing your request.');
    });

    it('understands legacy { message } bodies', () => {
        expect(extractApiError(axiosErrorWith({ message: 'legacy message' }), 'fallback')).toBe('legacy message');
    });

    it('understands legacy { error } bodies', () => {
        expect(extractApiError(axiosErrorWith({ error: 'legacy error text' }), 'fallback')).toBe('legacy error text');
    });

    it('passes plain-string bodies through', () => {
        expect(extractApiError(axiosErrorWith('Playback was stopped by an administrator.', 410), 'fallback'))
            .toBe('Playback was stopped by an administrator.');
    });

    it('returns the fallback for network errors, empty bodies, and non-axios errors', () => {
        expect(extractApiError(new AxiosError('Network Error', 'ERR_NETWORK'), 'fallback')).toBe('fallback');
        expect(extractApiError(axiosErrorWith(''), 'fallback')).toBe('fallback');
        expect(extractApiError(axiosErrorWith({}), 'fallback')).toBe('fallback');
        expect(extractApiError(new Error('plain'), 'fallback')).toBe('fallback');
        expect(extractApiError(undefined, 'fallback')).toBe('fallback');
    });

    it('never renders "[object Object]" for validation problem bodies', () => {
        const err = axiosErrorWith({
            title: 'One or more validation errors occurred.',
            status: 400,
            errors: { limit: ["The value 'abc' is not valid."] },
        });
        expect(extractApiError(err, 'fallback')).toBe('One or more validation errors occurred.');
    });
});

describe('extractApiErrorCode', () => {
    it('reads the machine discriminator from a ProblemDetails extension', () => {
        const err = axiosErrorWith({
            title: 'Password change required',
            status: 403,
            detail: 'You must change your password before continuing.',
            error: 'password_change_required',
        }, 403);
        expect(extractApiErrorCode(err)).toBe('password_change_required');
    });

    it('returns undefined when absent', () => {
        expect(extractApiErrorCode(axiosErrorWith({ title: 'x' }))).toBeUndefined();
        expect(extractApiErrorCode(new Error('plain'))).toBeUndefined();
    });
});
