import api from './api';
import type { MediaItem } from '../types';

/**
 * Wave E1 — playlist service. Mirrors the backend DTOs in PlaylistDtos.cs.
 *
 * Key design choices reflected in this surface:
 *   - Server playlists are persistent and owned. The audio queue (audioStore)
 *     is ephemeral and unrelated. `playPlaylist` hydrates a fresh queue from
 *     a playlist's tracks; it does not mutate the playlist.
 *   - Item identity in mutation calls is `playlistItemId` (the surrogate
 *     PlaylistItem.Id), not mediaItemId — duplicates of the same track are
 *     allowed within a playlist.
 */

export interface PlaylistSummary {
    id: string;
    name: string;
    description: string | null;
    isPublic: boolean;
    isOwner: boolean;
    ownerUsername: string;
    itemCount: number;
    createdAt: string;
    updatedAt: string;
}

export interface PlaylistEntry {
    playlistItemId: string;
    order: number;
    media: MediaItem;
}

export interface PlaylistDetail {
    id: string;
    name: string;
    description: string | null;
    isPublic: boolean;
    isOwner: boolean;
    ownerUsername: string;
    createdAt: string;
    updatedAt: string;
    items: PlaylistEntry[];
}

export const playlistService = {
    list: async (): Promise<PlaylistSummary[]> => {
        const { data } = await api.get<PlaylistSummary[]>('/playlists');
        return data;
    },

    get: async (id: string): Promise<PlaylistDetail> => {
        const { data } = await api.get<PlaylistDetail>(`/playlists/${id}`);
        return data;
    },

    create: async (request: { name: string; description?: string; isPublic?: boolean }): Promise<PlaylistSummary> => {
        const { data } = await api.post<PlaylistSummary>('/playlists', {
            name: request.name,
            description: request.description ?? null,
            isPublic: request.isPublic ?? false,
        });
        return data;
    },

    update: async (id: string, patch: { name?: string; description?: string | null; isPublic?: boolean }): Promise<void> => {
        await api.patch(`/playlists/${id}`, patch);
    },

    delete: async (id: string): Promise<void> => {
        await api.delete(`/playlists/${id}`);
    },

    /** Append the given track ids to the end of the playlist, preserving request order. */
    addItems: async (id: string, mediaItemIds: string[]): Promise<void> => {
        await api.post(`/playlists/${id}/items`, { mediaItemIds });
    },

    removeItem: async (id: string, playlistItemId: string): Promise<void> => {
        await api.delete(`/playlists/${id}/items/${playlistItemId}`);
    },

    /** itemIds must be a permutation of the playlist's current PlaylistItem.Ids. */
    reorder: async (id: string, itemIds: string[]): Promise<void> => {
        await api.put(`/playlists/${id}/order`, { itemIds });
    },
};
