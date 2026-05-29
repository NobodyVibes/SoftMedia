import api from './api';
import { type FileWatcherIssue } from '../types';

export interface BackupInfo {
    id: string;
    createdAtUtc: string;
    sizeBytes: number;
    isPinned: boolean;
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
     */
    async createBackup(): Promise<BackupInfo> {
        const response = await api.post<BackupInfo>('/admin/backup');
        return response.data;
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
        const response = await api.post<{ message: string }>('/admin/restore', form);
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
};
