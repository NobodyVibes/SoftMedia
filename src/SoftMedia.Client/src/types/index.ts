export interface Library {
    id: string;
    name: string;
    type: 'Movie' | 'TV' | 'Music' | 'Book' | 'Game' | 'Photo';
    paths: string[];
    order: number;
}

/** DV-WI-013 — one file-copy of a version group (detail responses; player switcher). */
export interface MediaVersion {
    id: string;
    label: string;
    width?: number;
    height?: number;
    hdrFormat?: string;
    bitrate?: number;
    container?: string;
    size: number;
    durationSeconds?: number;
    /** The computed primary (preferred override → max height → HDR → bitrate → newest). */
    isPrimary: boolean;
    /** The user's explicit "prefer this version" override, when set. */
    preferred: boolean;
    watched: boolean;
    playbackPosition?: number;
}

export interface MediaItem {
    id: string;
    libraryId: string;
    title: string;
    sortTitle: string;
    year?: number;
    dateAdded: string;
    /** Full release/capture date (photos: EXIF date taken). */
    releaseDate?: string;
    posterPath?: string;
    backdropPath?: string;
    quality?: 'SD' | 'HD' | '4K' | 'HDR';
    /** DV-WI-013 — shared id of all file-copies of this logical title; absent = no known siblings. */
    versionGroupId?: string;
    /** DV-WI-013 — server-derived quality label ("4K HDR10 Director's Cut"); render verbatim. */
    versionLabel?: string;
    /** DV-WI-013 — number of file-copies; 1 on list responses (only detail responses hydrate the group). */
    versionCount?: number;
    /** DV-WI-013 — the group's copies, primary-first; present on detail responses with versionCount > 1. */
    versions?: MediaVersion[];
    genres?: string[];
    rating?: string; // "TV-MA", "PG-13", etc. Actually DTO uses this for External Rating now.
    communityRating?: number; // Internal Average Rating
    progress?: number; // 0-100 for continue watching
    playbackPosition?: number; // Resume position in seconds
    description?: string;
    /** Producing organisation: studio/network for video, **publisher** for books. */
    studio?: string;
    /** Primary creator: director for video, **author** for books. Multi-author books also
     *  list every author in `cast` with the character "Author". */
    director?: string;
    /** Books only. Normalised to digits (no hyphens). */
    isbn?: string;
    /** Books only — display figure. Page navigation in the reader uses `BookInfo.pageCount`
     *  from `/books/{id}/info`, which counts the real document, not this. */
    pageCount?: number;
    container?: string;
    videoCodec?: string;
    audioCodec?: string;
    resolution?: string;
    // `unknown` rather than `any`: consumers must narrow (e.g. `as string`)
    // instead of silently treating provider metadata as whatever they hoped.
    metadata?: Record<string, unknown>;
    userRating?: number;
    personalRating?: number;
    isFavorite?: boolean;
    watched?: boolean;
    // Wave E3 — watchlist flag for the calling user.
    isWatchlisted?: boolean;
    type?: MediaType;
    seriesId?: string;
    seasonNumber?: number;
    episodeNumber?: number;
    artistId?: string;
    albumId?: string;
    trackNumber?: number;
    discNumber?: number;
    // SR-WI-063: the ONLY duration field — the server's formatted `duration` string is
    // gone. Render with formatRuntime() (lib/utils) where a "2h 15m 3s" label is wanted.
    durationSeconds?: number; // Raw duration in seconds

    // Timecode markers used by skip pills and the progress bar.
    // `*Source` distinguishes embedded chapters (always trusted) from
    // cross-episode auto-detection (best-effort, surfaced in debug panel).
    creditsStart?: number;
    creditsEnd?: number;
    creditsSource?: DetectionSource;
    introStart?: number;
    introEnd?: number;
    introSource?: DetectionSource;
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

    // Wave E2 — link to the parent collection / franchise for movies. Null
    // when the movie isn't part of any collection.
    collectionId?: string;

    // P3-WI-003 — admin metadata lock. True ⇒ auto-refresh skips this item.
    metadataLocked?: boolean;
    metadataLockedAt?: string | null;
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

/**
 * Source of an intro/credits timecode pair on a MediaItem. Mirrors the
 * server-side DetectionSource enum.
 */
export type DetectionSource = 'Chapter' | 'Detected';

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
    Track: 'Track',
    ComicSeries: 'ComicSeries',
    ComicIssue: 'ComicIssue'
} as const;

export type MediaType = typeof MediaType[keyof typeof MediaType];

export interface PagedResult<T> {
    items: T[];
    totalCount: number;
    page: number;
    pageSize: number;
}

// Library Scan Types
export type LibraryScanStatus = 'Queued' | 'Running' | 'Completed' | 'Failed' | 'Cancelled' | 'Paused';
export type LibraryScanStage = 'Pending' | 'Discovery' | 'Processing' | 'Metadata' | 'Finishing';
export type LibraryScanJobType = 'LibraryScan' | 'MetadataRefresh' | 'IntroCreditsDetection';

export interface LibraryScanJob {
    id: string;
    type: LibraryScanJobType;
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
    /** Items enqueued for metadata enrichment when the file walk finished (Metadata stage). */
    metadataTotal: number;
    /** Items still awaiting metadata enrichment (Metadata stage). */
    metadataRemaining: number;
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
