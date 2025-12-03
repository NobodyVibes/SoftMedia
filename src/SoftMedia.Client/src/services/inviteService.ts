import api from './api';

export interface InviteDto {
    code: string;
    createdAt: string;
    expiresAt: string | null;
    usedAt: string | null;
    usedByUsername: string | null;
    isRevoked: boolean;
}

export interface CreateInviteRequest {
    expiresInHours: number | null;
}

export const inviteService = {
    async createInvite(expiresInHours: number | null): Promise<InviteDto> {
        const response = await api.post<InviteDto>('/invites', { expiresInHours });
        return response.data;
    },

    async getInvites(): Promise<InviteDto[]> {
        const response = await api.get<InviteDto[]>('/invites');
        return response.data;
    },

    async revokeInvite(code: string): Promise<void> {
        await api.delete(`/invites/${code}`);
    },
};
