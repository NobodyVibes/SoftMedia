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

/** Mirrors PlaylistKind on the server. Manual playlists store rows; smart ones store a query. */
export type PlaylistKind = 'Manual' | 'Smart';

/** Mirrors SmartPlaylistSort. */
export type SmartPlaylistSort =
    | 'RecentlyAdded'
    | 'MostPlayed'
    | 'RecentlyPlayed'
    | 'Title'
    | 'Artist';

/**
 * The query behind a smart playlist (server: SmartPlaylistRules).
 *
 * Every play-related field is evaluated against the OWNER's own listening —
 * never the all-user aggregate on MediaItem — which is also why a smart playlist
 * cannot be made public.
 */
export interface SmartPlaylistRules {
    favoritesOnly?: boolean;
    unplayedOnly?: boolean;
    addedWithinDays?: number | null;
    genre?: string | null;
    artistId?: string | null;
    sort: SmartPlaylistSort;
    limit: number;
}

export const MAX_SMART_PLAYLIST_LIMIT = 500;

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
    /**
     * Up to four distinct album covers from the head of the playlist, in play
     * order — see PlaylistCover. Empty when no head track has artwork.
     */
    coverImagePaths: string[];
    kind: PlaylistKind;
    /** Smart playlists only, and only when the caller owns them. */
    rules: SmartPlaylistRules | null;
    /**
     * An uploaded cover. Also delivered as the sole entry of coverImagePaths, so
     * rendering needs no special case — this tells the UI a custom cover is in
     * play and can be removed.
     */
    coverImagePath: string | null;
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
    kind: PlaylistKind;
    /** Smart playlists only, and only when the caller owns them. */
    rules: SmartPlaylistRules | null;
    /** An uploaded cover; null means the mosaic is built from the tracks. */
    coverImagePath: string | null;
}

export interface ImportPlaylistResult {
    playlist: PlaylistSummary;
    matchedCount: number;
    unmatchedCount: number;
    /** A few of the lines that matched nothing, so the user can see why. */
    unmatchedSample: string[];
}

export const playlistService = {
    list: async (): Promise<PlaylistSummary[]> => {
        const { data } = await api.get<PlaylistSummary[]>('/playlists');
        return data;
    },

    /**
     * Playlist matches for the global search box. Separate from
     * searchService.globalSearch because playlists are not media items and are
     * not owned by a library, so they cannot ride in its per-library groups.
     */
    search: async (query: string, limit = 5): Promise<PlaylistSummary[]> => {
        if (!query || query.trim().length < 2) return [];
        const { data } = await api.get<PlaylistSummary[]>('/playlists/search', {
            params: { query, limit },
        });
        return data;
    },

    get: async (id: string): Promise<PlaylistDetail> => {
        const { data } = await api.get<PlaylistDetail>(`/playlists/${id}`);
        return data;
    },

    /** Passing `rules` creates a SMART playlist; omitting them creates a manual one. */
    create: async (request: {
        name: string;
        description?: string;
        isPublic?: boolean;
        rules?: SmartPlaylistRules;
    }): Promise<PlaylistSummary> => {
        const { data } = await api.post<PlaylistSummary>('/playlists', {
            name: request.name,
            description: request.description ?? null,
            isPublic: request.isPublic ?? false,
            rules: request.rules ?? null,
        });
        return data;
    },

    update: async (id: string, patch: {
        name?: string;
        description?: string | null;
        isPublic?: boolean;
        rules?: SmartPlaylistRules;
    }): Promise<void> => {
        await api.patch(`/playlists/${id}`, patch);
    },

    /**
     * The playlist as extended-M3U text. Entries are the tracks' paths ON THE
     * SERVER, which is what other media servers export and what a local player
     * pointed at the same library needs.
     */
    exportM3u: async (id: string): Promise<string> => {
        const { data } = await api.get<string>(`/playlists/${id}/export`, { responseType: 'text' });
        return data;
    },

    /** Replaces the generated mosaic with an uploaded image. Owner only. */
    uploadCover: async (id: string, file: File): Promise<string | null> => {
        const form = new FormData();
        form.append('file', file);
        const { data } = await api.post<{ coverImagePath: string | null }>(`/playlists/${id}/cover`, form);
        return data.coverImagePath;
    },

    /** Drops the uploaded cover, returning the playlist to its track mosaic. */
    deleteCover: async (id: string): Promise<void> => {
        await api.delete(`/playlists/${id}/cover`);
    },

    /** Creates a playlist from M3U text. Never a silent partial: see the counts. */
    importM3u: async (content: string, name?: string): Promise<ImportPlaylistResult> => {
        const { data } = await api.post<ImportPlaylistResult>('/playlists/import', { content, name: name ?? null });
        return data;
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
