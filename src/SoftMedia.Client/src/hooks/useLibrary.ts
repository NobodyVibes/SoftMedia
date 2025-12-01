import { useQuery, useInfiniteQuery } from '@tanstack/react-query';
import { libraryService } from '../services/libraryService';

export const useLibraries = () => {
    return useQuery({
        queryKey: ['libraries'],
        queryFn: libraryService.getAll,
    });
};

export const useLibrary = (id: string) => {
    return useQuery({
        queryKey: ['library', id],
        queryFn: () => libraryService.getById(id),
        enabled: !!id,
    });
};

export const useLibraryItems = (
    libraryId: string,
    search?: string,
    sortBy?: string
) => {
    return useInfiniteQuery({
        queryKey: ['libraryItems', libraryId, search, sortBy],
        queryFn: ({ pageParam = 1 }) =>
            libraryService.getItems(libraryId, pageParam as number, 50, search, sortBy),
        initialPageParam: 1,
        getNextPageParam: (lastPage) => {
            const nextPage = lastPage.page + 1;
            const totalPages = Math.ceil(lastPage.totalCount / lastPage.pageSize);
            return nextPage <= totalPages ? nextPage : undefined;
        },
        enabled: !!libraryId,
    });
};

export const useRecentMedia = (limit: number = 20, type?: string) => {
    return useQuery({
        queryKey: ['recentMedia', limit, type],
        queryFn: () => libraryService.getRecent(limit, type),
    });
};
