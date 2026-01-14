import api from './api';

export const accountService = {
    /**
     * Change the current user's password
     * Uses the existing /auth/change-password endpoint
     */
    changePassword: async (oldPassword: string, newPassword: string): Promise<string> => {
        const response = await api.post<string>('/auth/change-password', {
            oldPassword,
            newPassword
        });
        return response.data;
    },

    /**
     * Delete the current user's account (soft delete)
     */
    deleteAccount: async (): Promise<void> => {
        await api.delete('/account');
    },
};
