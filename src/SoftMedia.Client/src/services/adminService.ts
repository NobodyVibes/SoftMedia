import api from './api';
import { type FileWatcherIssue } from '../types';

export interface BackupInfo {
    id: string;
    createdAtUtc: string;
    sizeBytes: number;
    isPinned: boolean;
    /** Editable display label; defaults to the id. */
    name: string;
}

export interface ArtworkRepairResult {
    itemsScanned: number;
    missingImages: number;
    itemsReEnqueued: number;
    lockedSkipped: number;
    needsRescan: number;
    failedEnqueue: number;
}

export interface ScheduledTaskStatus {
    name: string;
    description: string;
    schedule: 'Scheduled' | 'EventDriven';
    supportsManualTrigger: boolean;
    lastRunUtc: string | null;
    lastRunDurationMs: number | null;
    lastResult: string | null;
    lastError: string | null;
    nextRunUtc: string | null;
}

// --- Manual metadata fix (P3-WI-003) ---

export interface MetadataSearchCandidate {
    providerName: string;
    providerItemId: string;
    title: string;
    year: number | null;
    posterUrl: string | null;
    subtitle: string | null;
}

/** R-WI-016 — one row of the admin Now-Playing dashboard. */
export interface ActiveSession {
    type: 'Transcode' | 'Remux' | 'DirectPlay';
    state: string;
    userId: string;
    userName: string;
    mediaId: string;
    mediaTitle: string;
    positionSeconds: number;
    durationSeconds: number;
    startedAt: string;
    resolution: string | null;
    codec: string | null;
    maxBitrateKbps: number | null;
    canTerminate: boolean;
    subtitleTrackIndex: number | null;
    streamId: string | null;
}

export const adminService = {
    /**
     * Gets all current file watcher issues.
     */
    async getFileWatcherIssues(): Promise<FileWatcherIssue[]> {
        const response = await api.get<FileWatcherIssue[]>('/admin/file-watcher-issues');
        return response.data;
    },

    /**
     * Retries a file that previously had issues.
     */
    async retryFile(path: string): Promise<void> {
        await api.post('/admin/file-watcher-issues/retry', { path });
    },

    /**
     * Clears/dismisses a file watcher issue.
     */
    async clearIssue(path: string): Promise<void> {
        await api.delete('/admin/file-watcher-issues', { params: { path } });
    },

    /**
     * Manually triggers an update of the hero section cache.
     */
    async refreshHeroCache(): Promise<void> {
        await api.post('/admin/hero-cache/refresh');
    },

    /**
     * Lists database backups on the server, newest first.
     */
    async listBackups(): Promise<BackupInfo[]> {
        const response = await api.get<BackupInfo[]>('/admin/backup');
        return response.data;
    },

    /**
     * Creates a new database backup on the server and returns its metadata.
     * An optional display name can be supplied.
     */
    async createBackup(name?: string): Promise<BackupInfo> {
        const response = await api.post<BackupInfo>('/admin/backup', { name: name ?? null });
        return response.data;
    },

    /**
     * Renames a backup's display label (the archive id is unchanged).
     */
    async renameBackup(id: string, name: string): Promise<void> {
        await api.patch(`/admin/backup/${encodeURIComponent(id)}/name`, { name });
    },

    /**
     * Permanently deletes a backup archive.
     */
    async deleteBackup(id: string): Promise<void> {
        await api.delete(`/admin/backup/${encodeURIComponent(id)}`);
    },

    /**
     * Downloads a backup archive by id. Uses a blob + object URL so the bearer
     * token rides in the Authorization header rather than the navigation URL.
     */
    async downloadBackup(id: string): Promise<{ blob: Blob; filename: string }> {
        const response = await api.get(`/admin/backup/${encodeURIComponent(id)}/download`, {
            responseType: 'blob',
        });
        return { blob: response.data, filename: `${id}.zip` };
    },

    /**
     * Pins or unpins a backup so rotation never deletes it.
     */
    async setBackupPinned(id: string, pinned: boolean): Promise<void> {
        const url = `/admin/backup/${encodeURIComponent(id)}/pin`;
        if (pinned) await api.post(url);
        else await api.delete(url);
    },

    /**
     * Uploads a backup archive to stage a restore on next server restart.
     */
    async restoreBackup(file: File): Promise<{ message: string }> {
        const form = new FormData();
        form.append('file', file);
        // The shared axios instance defaults Content-Type to application/json. When a
        // request carries that content type, axios serialises a FormData body to JSON
        // (helpers/formDataToJSON) — which drops the File entirely, so no multipart
        // body ever reaches the server and IFormFile binds to null. Overriding the
        // content type here keeps the FormData intact; the browser then fills in the
        // real `multipart/form-data; boundary=...` header.
        const response = await api.post<{ message: string }>('/admin/restore', form, {
            headers: { 'Content-Type': 'multipart/form-data' },
        });
        return response.data;
    },

    /**
     * Re-fetches artwork that a database-only restore couldn't bring back (backups
     * exclude the on-disk image cache). Re-queues affected items for metadata
     * enrichment; posters fill in as the downloads complete.
     */
    async repairArtwork(): Promise<ArtworkRepairResult> {
        const response = await api.post<ArtworkRepairResult>('/admin/repair-artwork');
        return response.data;
    },

    /**
     * Lists background tasks with last-run telemetry.
     */
    async listTasks(): Promise<ScheduledTaskStatus[]> {
        const response = await api.get<ScheduledTaskStatus[]>('/admin/tasks');
        return response.data;
    },

    /**
     * Manually triggers a task that supports it (e.g. metadata refresh).
     */
    async triggerTask(name: string): Promise<void> {
        await api.post(`/admin/tasks/${encodeURIComponent(name)}/trigger`);
    },

    // --- Now Playing (R-WI-016) ---

    /** Active playback sessions: transcodes/remuxes + direct plays. */
    async getActiveSessions(): Promise<ActiveSession[]> {
        const response = await api.get<ActiveSession[]>('/admin/sessions');
        return response.data;
    },

    /**
     * Terminate a TRANSCODE session by its full session key (kills ffmpeg and frees
     * the user's concurrency-cap slot). Direct plays are read-only in v1.
     */
    async terminateSession(session: ActiveSession): Promise<void> {
        await api.delete('/admin/sessions', {
            params: {
                mediaId: session.mediaId,
                userId: session.userId,
                sub: session.subtitleTrackIndex ?? undefined,
                sid: session.streamId ?? undefined,
            },
        });
    },

    // --- Manual metadata fix (P3-WI-003) ---

    async searchMatch(itemId: string, query: string, year?: number | null): Promise<MetadataSearchCandidate[]> {
        const response = await api.post<MetadataSearchCandidate[]>(`/admin/match/${itemId}/search`, { query, year });
        return response.data;
    },

    async applyMatch(itemId: string, providerName: string, providerItemId: string): Promise<void> {
        await api.post(`/admin/match/${itemId}/apply`, { providerName, providerItemId });
    },

    async manualEditMatch(itemId: string, edits: { title?: string; overview?: string; year?: number; posterUrl?: string; contentRating?: string }): Promise<void> {
        await api.patch(`/admin/match/${itemId}`, edits);
    },

    async unlockMatch(itemId: string): Promise<void> {
        await api.post(`/admin/match/${itemId}/unlock`);
    },
};
