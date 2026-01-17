import api from './api';

export interface SystemNotification {
    id: string;
    type: string;
    title: string;
    message: string;
    severity: 'info' | 'warning' | 'error';
    createdAt: string;
    dismissedAt: string | null;
    dismissedBy: string | null;
    metadata: string | null;
}

export interface OMDbUsage {
    used: number;
    limit: number;
    tier: string;
    isExhausted: boolean;
    resetTimeUtc: string;
}

export const notificationService = {
    /**
     * Gets all active (non-dismissed) system notifications.
     */
    async getNotifications(): Promise<SystemNotification[]> {
        const response = await api.get<SystemNotification[]>('/notifications');
        return response.data;
    },

    /**
     * Dismisses a notification.
     */
    async dismissNotification(id: string): Promise<void> {
        await api.post(`/notifications/${id}/dismiss`);
    },

    /**
     * Gets OMDb API usage information.
     */
    async getOMDbUsage(): Promise<OMDbUsage> {
        const response = await api.get<OMDbUsage>('/admin/omdb-usage');
        return response.data;
    }
};
