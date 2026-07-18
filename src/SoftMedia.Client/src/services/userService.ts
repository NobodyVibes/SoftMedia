import api from './api';

export interface UserDto {
    id: string;
    username: string;
    role: string;
    maxRating: string;
    createdAt: string;
    isBanned: boolean;
    isApproved: boolean;
    isRejected: boolean;
    contentRatings: Record<string, string>;
    firstName: string;
    lastName: string;
    createdByAdmin: boolean;
    usedInviteCode: string | null;
    twoFactorEnabled: boolean;
    /** R-WI-009: per-user max streaming bitrate in kbps (0 = unlimited). */
    maxStreamBitrateKbps: number;
}

export interface UpdateUserRoleRequest {
    role: string;
}

export interface BanUserRequest {
    isBanned: boolean;
}

export const userService = {
    async getUsers(): Promise<UserDto[]> {
        const response = await api.get<UserDto[]>('/users');
        return response.data;
    },

    async updateUserRole(userId: string, role: string): Promise<void> {
        await api.put(`/users/${userId}/role`, { role });
    },

    async banUser(userId: string, isBanned: boolean): Promise<void> {
        await api.put(`/users/${userId}/ban`, { isBanned });
    },

    async approveUser(userId: string, isApproved: boolean): Promise<void> {
        await api.put(`/users/${userId}/approve`, { isApproved });
    },

    async denyUser(userId: string): Promise<void> {
        await api.put(`/users/${userId}/deny`, {});
    },

    async deleteUser(userId: string): Promise<void> {
        await api.delete(`/users/${userId}`);
    },

    // R-WI-011: contentRatings lets the admin set content ceilings at creation.
    // Omitted = no restrictions (the default — new users are never capped implicitly).
    async createUser(data: { username: string; password: string; role: string; firstName: string; lastName: string; contentRatings?: Record<string, string> }): Promise<UserDto> {
        const response = await api.post<UserDto>('/users', data);
        return response.data;
    },

    async updateUserRatings(userId: string, contentRatings: Record<string, string>): Promise<void> {
        await api.put(`/users/${userId}/ratings`, { contentRatings });
    },

    // R-WI-009: set the per-user streaming bitrate cap (kbps; 0 = unlimited). Admin-only.
    async updateUserStreaming(userId: string, maxStreamBitrateKbps: number): Promise<void> {
        await api.put(`/users/${userId}/streaming`, { maxStreamBitrateKbps });
    },

    async resetUserPassword(userId: string, newPassword: string): Promise<void> {
        await api.put(`/users/${userId}/password`, { newPassword });
    },

    // Admin recovery: clears a user's 2FA enrollment + remembered devices (admin-only,
    // enforced server-side on AdminController). For users locked out of their authenticator.
    async disableUserTwoFactor(userId: string): Promise<void> {
        await api.post(`/admin/users/${userId}/disable-2fa`);
    },

    // Wave C — per-user library ACL. Empty array means "unrestricted" (default).
    async getUserLibraryAccess(userId: string): Promise<string[]> {
        const response = await api.get<string[]>(`/users/${userId}/library-access`);
        return response.data;
    },

    async setUserLibraryAccess(userId: string, libraryIds: string[]): Promise<void> {
        await api.put(`/users/${userId}/library-access`, { libraryIds });
    }
};
