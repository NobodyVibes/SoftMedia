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
    }
};
