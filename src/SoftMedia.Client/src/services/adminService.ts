import api from './api';
import { type FileWatcherIssue } from '../types';

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
    }
};
