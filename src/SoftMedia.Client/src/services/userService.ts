import api from './api';

export interface UserDto {
    id: string;
    username: string;
    role: string;
    maxRating: string;
    createdAt: string;
    isBanned: boolean;
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

    async deleteUser(userId: string): Promise<void> {
        await api.delete(`/users/${userId}`);
    },
};
