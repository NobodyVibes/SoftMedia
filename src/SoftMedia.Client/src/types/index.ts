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
    duration?: string | number; // "2h 15m" format or seconds
    quality?: 'SD' | 'HD' | '4K' | 'HDR';
    genres?: string[];
    rating?: string; // "TV-MA", "PG-13", etc.
    progress?: number; // 0-100 for continue watching
    description?: string;
    container?: string;
    metadata?: Record<string, any>;
    userRating?: number;
    isFavorite?: boolean;
    watched?: boolean;
    type?: MediaType;
    seriesId?: string;
    seasonNumber?: number;
    episodeNumber?: number;
    artistId?: string;
    albumId?: string;
    trackNumber?: number;
    discNumber?: number;
}

export const MediaType = {
    Movie: 0,
    Series: 1,
    Episode: 2,
    Audio: 3,
    Book: 4,
    Game: 5,
    Photo: 6,
    Artist: 7,
    Album: 8
} as const;

export type MediaType = typeof MediaType[keyof typeof MediaType];

export interface PagedResult<T> {
    items: T[];
    totalCount: number;
    page: number;
    pageSize: number;
}

