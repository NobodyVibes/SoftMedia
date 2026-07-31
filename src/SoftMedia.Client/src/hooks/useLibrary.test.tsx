import { renderHook, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { useLibraryItems } from './useLibrary';
import { libraryService } from '../services/libraryService';
import { createWrapper } from '../test/wrapper';

// Mock libraryService
vi.mock('../services/libraryService', () => ({
    libraryService: {
        getItems: vi.fn(),
    },
}));

describe('useLibraryItems', () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    it('fetches library items successfully', async () => {
        const mockData = {
            items: [
                { id: '1', title: 'Movie 1', type: 'Movie' },
                { id: '2', title: 'Movie 2', type: 'Movie' },
            ],
            totalCount: 2,
            page: 1,
            pageSize: 50,
        };

        // `as never`: the fixture rows carry only the fields this hook touches,
        // not the full MediaItem shape.
        vi.mocked(libraryService.getItems).mockResolvedValue(mockData as never);

        const wrapper = createWrapper();
        const { result } = renderHook(() => useLibraryItems('lib-1'), { wrapper });

        await waitFor(() => expect(result.current.isSuccess).toBe(true));

        expect(result.current.data?.pages[0]).toEqual(mockData);
        expect(libraryService.getItems).toHaveBeenCalledWith('lib-1', 1, 50, undefined, undefined);
    });

    it('handles search and sort parameters', async () => {
        const mockData = { items: [], totalCount: 0, page: 1, pageSize: 50 };
        vi.mocked(libraryService.getItems).mockResolvedValue(mockData);

        const wrapper = createWrapper();
        const { result } = renderHook(() => useLibraryItems('lib-1', 'test query', 'title_asc'), { wrapper });

        await waitFor(() => expect(result.current.isSuccess).toBe(true));

        expect(libraryService.getItems).toHaveBeenCalledWith('lib-1', 1, 50, 'test query', 'title_asc');
    });
});
