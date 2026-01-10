import api from './api';

export interface AppSetting {
    key: string;
    value: string;
    description?: string;
    group: string;
}

export const settingsService = {
    getAll: async (): Promise<AppSetting[]> => {
        const response = await api.get<AppSetting[]>('/settings');
        return response.data;
    },

    update: async (settings: AppSetting[]): Promise<void> => {
        await api.put('/settings', settings);
    },

    /**
     * Trigger immediate metadata refresh for ongoing (Running) TV series
     */
    triggerMetadataRefresh: async (): Promise<{ message: string }> => {
        const response = await api.post<{ message: string }>('/settings/refresh-metadata');
        return response.data;
    }
};
