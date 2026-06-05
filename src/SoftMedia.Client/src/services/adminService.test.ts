import { describe, it, expect, afterEach } from 'vitest';
import type { AxiosAdapter } from 'axios';
import api from './api';
import { adminService } from './adminService';

// Regression guard for the "restore fails immediately" bug: the shared axios
// instance defaults Content-Type to application/json, and axios serialises a
// FormData body to JSON when the content type is JSON — silently dropping the
// uploaded File so the server's IFormFile binds to null (HTTP 400). The fix
// overrides the content type so the body stays multipart FormData.
describe('adminService.restoreBackup', () => {
    const originalAdapter = api.defaults.adapter;
    afterEach(() => {
        api.defaults.adapter = originalAdapter;
    });

    it('uploads the backup as multipart FormData, not JSON', async () => {
        let capturedData: unknown;
        const adapter: AxiosAdapter = async (config) => {
            // config.data is the request body AFTER axios runs transformRequest.
            capturedData = config.data;
            return {
                data: { message: 'Restore staged.' },
                status: 202,
                statusText: 'Accepted',
                headers: {},
                config,
            };
        };
        api.defaults.adapter = adapter;

        const file = new File([new Uint8Array([0x50, 0x4b, 0x03, 0x04])], 'backup.zip', {
            type: 'application/zip',
        });

        const result = await adminService.restoreBackup(file);

        // The body must reach the adapter as FormData. On the buggy path it would
        // be a JSON string like '{"file":{}}' with the file dropped.
        expect(capturedData).toBeInstanceOf(FormData);
        expect(typeof capturedData).not.toBe('string');
        expect(result.message).toBe('Restore staged.');
    });
});

describe('adminService.repairArtwork', () => {
    const originalAdapter = api.defaults.adapter;
    afterEach(() => {
        api.defaults.adapter = originalAdapter;
    });

    it('POSTs to /admin/repair-artwork and returns the repair counts', async () => {
        let capturedUrl: string | undefined;
        let capturedMethod: string | undefined;
        const adapter: AxiosAdapter = async (config) => {
            capturedUrl = config.url;
            capturedMethod = config.method;
            return {
                data: { itemsScanned: 10, missingImages: 4, itemsReEnqueued: 3, lockedSkipped: 1, needsRescan: 0 },
                status: 200,
                statusText: 'OK',
                headers: {},
                config,
            };
        };
        api.defaults.adapter = adapter;

        const result = await adminService.repairArtwork();

        expect(capturedUrl).toBe('/admin/repair-artwork');
        expect(capturedMethod).toBe('post');
        expect(result.itemsReEnqueued).toBe(3);
        expect(result.missingImages).toBe(4);
        expect(result.lockedSkipped).toBe(1);
    });
});
