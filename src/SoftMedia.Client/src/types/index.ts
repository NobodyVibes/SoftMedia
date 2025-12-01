export interface Library {
    id: string;
    name: string;
    type: 'Movie' | 'TV' | 'Music' | 'Book' | 'Game' | 'Photo';
    paths: string[];
}

export interface MediaItem {
    id: string;
    libraryId: string;
    title: string;
    sortTitle: string;
    path?: string; // Added for reader detection
    year?: number;
    dateAdded: string;
    posterPath?: string;
    backdropPath?: string;
    duration?: string; // "2h 15m" format
    quality?: 'SD' | 'HD' | '4K' | 'HDR';
    genres?: string[];
    rating?: string; // "TV-MA", "PG-13", etc.
    progress?: number; // 0-100 for continue watching
    description?: string;
    container?: string;
}

export interface PagedResult<T> {
    items: T[];
    totalCount: number;
    page: number;
    pageSize: number;
}

