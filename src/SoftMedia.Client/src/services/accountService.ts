import api from './api';

export interface TrustedDeviceDto {
    id: string;
    label: string | null;
    createdAtUtc: string;
    lastSeenAtUtc: string;
    lastVerifiedAtUtc: string;
}

export interface ApiTokenDto {
    id: string;
    label: string;
    scopes: string[];
    createdAt: string;
    lastUsedAt: string | null;
    lastUsedIp: string | null;
    expiresAt: string | null;
}

export interface CreateApiTokenResponse {
    id: string;
    token: string;
    label: string;
}

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

    /**
     * Lists the current user's active API tokens (never the raw secret).
     */
    listApiTokens: async (): Promise<ApiTokenDto[]> => {
        const response = await api.get<ApiTokenDto[]>('/account/api-tokens');
        return response.data;
    },

    /**
     * Mints a new API token. The raw token is returned exactly once.
     */
    createApiToken: async (
        label: string,
        scopes: string[],
        expiresAt: string | null
    ): Promise<CreateApiTokenResponse> => {
        const response = await api.post<CreateApiTokenResponse>('/account/api-tokens', {
            label,
            scopes,
            expiresAt,
        });
        return response.data;
    },

    /**
     * Revokes one of the current user's API tokens.
     */
    revokeApiToken: async (id: string): Promise<void> => {
        await api.delete(`/account/api-tokens/${id}`);
    },

    // --- TOTP 2FA (P2-WI-005) ---

    getTotpStatus: async (): Promise<{ enabled: boolean }> => {
        const response = await api.get<{ enabled: boolean }>('/account/totp');
        return response.data;
    },

    enrollTotp: async (): Promise<{ secret: string; otpAuthUri: string }> => {
        const response = await api.post<{ secret: string; otpAuthUri: string }>('/account/totp/enroll');
        return response.data;
    },

    confirmTotp: async (code: string): Promise<{ recoveryCodes: string[] }> => {
        const response = await api.post<{ recoveryCodes: string[] }>('/account/totp/enroll/confirm', { code });
        return response.data;
    },

    disableTotp: async (password: string, code: string): Promise<void> => {
        await api.post('/account/totp/disable', { password, code });
    },

    // --- Trusted devices (2FA expiration window) ---

    getTrustedDevices: async (): Promise<TrustedDeviceDto[]> => {
        const response = await api.get<TrustedDeviceDto[]>('/account/trusted-devices');
        return response.data;
    },

    revokeTrustedDevice: async (id: string): Promise<void> => {
        await api.delete(`/account/trusted-devices/${encodeURIComponent(id)}`);
    },

    revokeAllTrustedDevices: async (): Promise<void> => {
        await api.delete('/account/trusted-devices');
    },

    // --- Webhooks (P2-WI-004) ---

    listWebhooks: async (): Promise<WebhookDto[]> => {
        const response = await api.get<WebhookDto[]>('/webhooks');
        return response.data;
    },

    createWebhook: async (url: string, events: string[]): Promise<CreateWebhookResponse> => {
        const response = await api.post<CreateWebhookResponse>('/webhooks', { url, events });
        return response.data;
    },

    deleteWebhook: async (id: string): Promise<void> => {
        await api.delete(`/webhooks/${id}`);
    },

    testWebhook: async (id: string): Promise<void> => {
        await api.post(`/webhooks/${id}/test`);
    },
};

export interface WebhookDto {
    id: string;
    url: string;
    events: string[];
    active: boolean;
    createdAt: string;
    lastDeliveryAt: string | null;
    lastDeliveryStatus: string | null;
}

export interface CreateWebhookResponse {
    id: string;
    url: string;
    events: string[];
    secret: string;
}
