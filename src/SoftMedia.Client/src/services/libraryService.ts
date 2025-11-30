import api from './api';
import { type Library, type MediaItem, type PagedResult } from '../types';

export const libraryService = {
    getAll: async (): Promise<Library[]> => {
        const response = await api.get<Library[]>('/libraries');
        return response.data;
    },

    getById: async (id: string): Promise<Library> => {
        const response = await api.get<Library>(`/libraries/${id}`);
        return response.data;
    },

    getItems: async (
        libraryId: string,
        page: number = 1,
        pageSize: number = 50,
        search?: string,
        sortBy?: string
    ): Promise<PagedResult<MediaItem>> => {
        const params = new URLSearchParams({
            page: page.toString(),
            pageSize: pageSize.toString(),
        });

        if (search) params.append('search', search);
        if (sortBy) params.append('sortBy', sortBy);

        const response = await api.get<PagedResult<MediaItem>>(`/libraries/${libraryId}/items`, { params });
        return response.data;
    }
};
