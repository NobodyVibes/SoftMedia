import api from './api';

export interface UserPreferences {
    [key: string]: string;
}

export const userPreferencesService = {
    /**
     * Get the current user's preferences
     */
    getPreferences: async (): Promise<UserPreferences> => {
        const response = await api.get<UserPreferences>('/userpreferences');
        return response.data;
    },

    /**
     * Update the current user's preferences
     */
    updatePreferences: async (preferences: UserPreferences): Promise<void> => {
        await api.put('/userpreferences', { preferences });
    },
};
