export interface Library {
    id: string;
    name: string;
    type: 'Movie' | 'TV' | 'Music' | 'Book' | 'Game' | 'Photo';
    paths: string[];
    order: number;
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
    rating?: string; // "TV-MA", "PG-13", etc. Actually DTO uses this for External Rating now.
    communityRating?: number; // Internal Average Rating
    progress?: number; // 0-100 for continue watching
    playbackPosition?: number; // Resume position in seconds
    description?: string;
    container?: string;
    videoCodec?: string;
    audioCodec?: string;
    resolution?: string;
    metadata?: Record<string, any>;
    userRating?: number;
    personalRating?: number;
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
    durationSeconds?: number; // Raw duration in seconds (for audio player)

    // Timecode markers
    creditsStart?: number; // Seconds from start where credits begin
    chapters?: Chapter[]; // All chapter markers

    // Phase 2: Extended Quality Metadata
    bitDepth?: number; // 8, 10, 12 bit
    hdrFormat?: string; // "HDR10", "HDR10+", "Dolby Vision", "HLG", null for SDR
    audioChannels?: number; // Primary audio channel count
    bitrate?: number; // bits/second
    frameRate?: number; // fps
    width?: number; // Video width in pixels
    height?: number; // Video height in pixels
    audioTracks?: AudioTrack[]; // All audio tracks
    subtitleTracks?: SubtitleTrack[]; // All subtitle tracks
    cast?: CastMember[];
}

export interface CastMember {
    id: number;
    externalId?: number;
    name: string;
    imageUrl?: string;
    characters: string[];
    order: number;
}

export interface Chapter {
    startTime: number;
    title: string;
}

// Phase 2: Extended track info types
export interface AudioTrack {
    index: number;
    codec?: string;
    language?: string;
    channels: number;
    channelLayout?: string; // "stereo", "5.1", "7.1"
    title?: string;
    isDefault: boolean;
}

export interface SubtitleTrack {
    index: number;
    codec?: string; // "srt", "ass", "pgs"
    language?: string;
    title?: string;
    isDefault: boolean;
    isForced: boolean;
}

export const MediaType = {
    Movie: 'Movie',
    Series: 'Series',
    Episode: 'Episode',
    Audio: 'Audio',
    Book: 'Book',
    Game: 'Game',
    Photo: 'Photo',
    Season: 'Season',
    Artist: 'Artist',
    Album: 'Album',
    Track: 'Track'
} as const;

export type MediaType = typeof MediaType[keyof typeof MediaType];

export interface PagedResult<T> {
    items: T[];
    totalCount: number;
    page: number;
    pageSize: number;
}

// Library Scan Types
export type LibraryScanStatus = 'Queued' | 'Running' | 'Completed' | 'Failed' | 'Cancelled';
export type LibraryScanStage = 'Pending' | 'Discovery' | 'Processing' | 'Metadata' | 'Finishing';

export interface LibraryScanJob {
    id: string;
    libraryId: string;
    libraryName: string;
    status: LibraryScanStatus;
    stage: LibraryScanStage;
    totalFiles: number;
    processedFiles: number;
    newItems: number;
    updatedItems: number;
    skippedItems: number;
    errorCount: number;
    currentFile: string | null;
    errorMessage: string | null;
    startedAt: string;
    completedAt: string | null;
    queuePosition: number;
    progressPercent: number;
}

// File Watcher Issue Types
export interface FileWatcherIssue {
    path: string;
    fileName: string;
    status: string;
    firstSeen: string;
    lastChecked: string;
    libraryId: string;
    libraryName: string;

    canRetry: boolean;
}

// Player Types
export interface TrackInfo {
    index: number;
    type: string;
    language?: string;
    title?: string;
    codec?: string;
    isDefault: boolean;
}

export interface TracksResponse {
    audioTracks: TrackInfo[];
    subtitleTracks: TrackInfo[];
}
