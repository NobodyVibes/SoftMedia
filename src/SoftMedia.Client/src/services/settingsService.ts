import api from './api';

export interface AppSetting {
    key: string;
    value: string;
    description?: string;
    group: string;
}

/**
 * Re-sync the admin's local settings draft from a fresh server snapshot WITHOUT discarding
 * their unsaved edits. The settings page and self-contained admin cards (e.g. DlnaSettingsCard)
 * both read the shared ['settings'] query; when a card saves and invalidates that query, the page
 * must NOT be remounted, so a naive `setLocalSettings(server)` would revert edits the admin made in
 * another group. We do a 3-way merge instead: a key adopts the server value only if it actually
 * changed since the last snapshot (or is new); otherwise the local (possibly edited) value is kept.
 * On first load (`prevServer` null) the server copy is adopted wholesale.
 */
export function mergeSettingsPreservingEdits(
    prevServer: AppSetting[] | null,
    nextServer: AppSetting[],
    local: AppSetting[],
): AppSetting[] {
    if (!prevServer) return nextServer;
    const prevByKey = new Map(prevServer.map(s => [s.key, s.value]));
    const localByKey = new Map(local.map(s => [s.key, s.value]));
    return nextServer.map(srv => {
        const serverChanged = !prevByKey.has(srv.key) || prevByKey.get(srv.key) !== srv.value;
        if (serverChanged || !localByKey.has(srv.key)) return srv;
        // Server value unchanged since last sync: keep the local edit, but take fresh metadata.
        return { ...srv, value: localByKey.get(srv.key)! };
    });
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
